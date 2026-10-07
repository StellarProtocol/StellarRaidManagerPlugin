using System;
using System.Collections.Generic;

namespace Stellar.RaidManager;

// Callout LIST order ("List order" dropdown, config mech_order; the minimap is unaffected). Flatten() turns the live
// rows into header + row lines in one of three orders:
//   • Arrival (default) — groups and rows in the order they first ARRIVED: a new row goes to the bottom of its group,
//     a new group to the bottom of the list, and nothing already shown moves when something new appears. A row's
//     arrival is its McRow creation (Reconcile); a row that disappears and later returns is a NEW arrival (new
//     object). A group's arrival is the first time any of its rows was present; it is forgotten once the group has no
//     rows, so a returning group is a new arrival too.
//   • Urgent — rows by SHOWN remaining time ascending: "NOW" rows first, then timed rows, untimed rows last; a group sits
//     at the position of its most urgent row, rows inside by urgency. Arrival breaks every tie (stable, no jitter).
//   • Table — the original upstream order: groups by table Order, rows by colour then label.
// Re-sorted every scan (≈ 5 Hz), so the urgency order follows the countdowns at the scan rate.
internal sealed partial class MechanicCalloutTracker
{
    public enum ListOrderMode { Arrival = 0, Urgent = 1, Table = 2 }

    public ListOrderMode ListOrder { get; set; } = ListOrderMode.Arrival;

    private long _arrivalSeq;                                           // per-row / per-group arrival counter
    private readonly Dictionary<string, long> _groupArrival = new();   // English group → arrival (while present)
    private readonly List<string> _groupGone = new();

    private long NextArrival() => ++_arrivalSeq;

    // Urgency sort key: NOW (hit window / release) first, then shown remaining seconds, untimed last.
    private static float Urgency(McRow r) =>
        !r.HasTimer ? float.PositiveInfinity : r.IsHitNow ? -1f : r.ShownRemainSec;

    private void Flatten()
    {
        _lines.Clear();
        var rows = new List<McRow>(_rows.Values);

        // Group arrivals: first presence; forgotten when the group has no rows this scan.
        var present = new HashSet<string>();
        foreach (var r in rows)
        {
            present.Add(r.Group);
            if (!_groupArrival.ContainsKey(r.Group)) _groupArrival[r.Group] = NextArrival();
        }
        _groupGone.Clear();
        foreach (var g in _groupArrival.Keys) if (!present.Contains(g)) _groupGone.Add(g);
        foreach (var g in _groupGone) _groupArrival.Remove(g);

        switch (ListOrder)
        {
            case ListOrderMode.Table:   SortTable(rows); break;
            case ListOrderMode.Urgent:  SortUrgent(rows); break;
            default:
                rows.Sort((a, b) =>
                {
                    int g = _groupArrival[a.Group].CompareTo(_groupArrival[b.Group]);
                    return g != 0 ? g : a.Arrival.CompareTo(b.Arrival);
                });
                break;
        }

        string? lastGroup = null;
        foreach (var r in rows)
        {
            if (r.Group != lastGroup) { _lines.Add(new McLine(McText.T(r.Group))); lastGroup = r.Group; }
            _lines.Add(new McLine(r));
        }
        RowCount = rows.Count;
    }

    // Groups by their lowest Order (table position; rules use 100+), rows inside by colour then label (upstream sort).
    private static void SortTable(List<McRow> rows)
    {
        var groupOrder = new Dictionary<string, int>();
        foreach (var r in rows)
            if (!groupOrder.TryGetValue(r.Group, out int o) || r.Order < o) groupOrder[r.Group] = r.Order;
        rows.Sort((a, b) =>
        {
            int g = groupOrder[a.Group].CompareTo(groupOrder[b.Group]);
            if (g != 0) return g;
            int gs = string.CompareOrdinal(a.Group, b.Group);
            if (gs != 0) return gs;
            int c = a.Color.CompareTo(b.Color);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
    }

    // A group's key = its most urgent row (urgency, then that row's arrival); rows inside by (urgency, arrival).
    private void SortUrgent(List<McRow> rows)
    {
        var urg = new Dictionary<McRow, float>(rows.Count);
        var best = new Dictionary<string, (float U, long A)>();
        foreach (var r in rows)
        {
            float u = urg[r] = Urgency(r);
            if (!best.TryGetValue(r.Group, out var b) || u < b.U || (u == b.U && r.Arrival < b.A)) best[r.Group] = (u, r.Arrival);
        }
        rows.Sort((a, b) =>
        {
            if (a.Group != b.Group)
            {
                var ga = best[a.Group]; var gb = best[b.Group];
                int c = ga.U.CompareTo(gb.U);
                if (c != 0) return c;
                c = ga.A.CompareTo(gb.A);
                return c != 0 ? c : _groupArrival[a.Group].CompareTo(_groupArrival[b.Group]);
            }
            int u = urg[a].CompareTo(urg[b]);
            return u != 0 ? u : a.Arrival.CompareTo(b.Arrival);
        });
    }
}
