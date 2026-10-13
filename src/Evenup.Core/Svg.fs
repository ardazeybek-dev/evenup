namespace Evenup

open System
open System.Globalization
open System.Text

/// Dependency-free SVG rendering of a debt graph. Colours come from CSS classes
/// (`.eu-*`), so the same markup follows the page's light or dark theme.
module Svg =

    let private inv = CultureInfo.InvariantCulture
    let private f (x: float) = x.ToString("0.#", inv)

    let escape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")

    let private initial (name: string) =
        if String.IsNullOrEmpty name then "?"
        else Char.ToUpperInvariant(name[0]).ToString()

    /// Circle layout of people with curved arrows from debtor to creditor.
    /// Edge thickness follows the amount; nodes are coloured by balance sign.
    let graph (id: string) (people: (Person * int64) list) (edges: Transfer list) (fmt: int64 -> string) =
        let n = max 1 people.Length
        let radius = max 130.0 (float n * 24.0)
        let pad = 92.0
        let size = (radius + pad) * 2.0
        let cx, cy = size / 2.0, size / 2.0
        let nodeR = 25.0
        let pos =
            people
            |> List.mapi (fun i (p, _) ->
                if n = 1 then p, (cx, cy)
                else
                    let a = -Math.PI / 2.0 + 2.0 * Math.PI * float i / float n
                    p, (cx + radius * cos a, cy + radius * sin a))
            |> dict
        let maxAmount = edges |> List.fold (fun m e -> max m e.Amount) 1L
        let sb = StringBuilder()
        let w (s: string) = sb.Append s |> ignore
        w (sprintf "<svg class=\"eu-graph\" viewBox=\"0 0 %s %s\" role=\"img\" aria-labelledby=\"%s-t\" xmlns=\"http://www.w3.org/2000/svg\">" (f size) (f size) id)
        w (sprintf "<title id=\"%s-t\">%d people, %d transfers</title>" id people.Length edges.Length)
        w (sprintf "<defs><marker id=\"%s-arrow\" viewBox=\"0 0 10 10\" refX=\"8.5\" refY=\"5\" markerWidth=\"11\" markerHeight=\"11\" markerUnits=\"userSpaceOnUse\" orient=\"auto-start-reverse\"><path class=\"eu-arrow\" d=\"M0,0 L10,5 L0,10 z\"/></marker></defs>" id)
        let labels = StringBuilder()
        for e in edges do
            match pos.TryGetValue e.From, pos.TryGetValue e.To with
            | (true, (x1, y1)), (true, (x2, y2)) ->
                let dx, dy = x2 - x1, y2 - y1
                let len = max 1.0 (sqrt (dx * dx + dy * dy))
                // bend every edge to its left so A→B and B→A never overlap
                let bend = 0.16 * len
                let mx, my = (x1 + x2) / 2.0 - dy / len * bend, (y1 + y2) / 2.0 + dx / len * bend
                let towards (px, py) (qx, qy) d =
                    let vx, vy = qx - px, qy - py
                    let l = max 1.0 (sqrt (vx * vx + vy * vy))
                    px + vx / l * d, py + vy / l * d
                let sx, sy = towards (x1, y1) (mx, my) (nodeR + 2.0)
                let ex, ey = towards (x2, y2) (mx, my) (nodeR + 5.0)
                let width = 1.6 + 5.4 * float e.Amount / float maxAmount
                w (sprintf "<path class=\"eu-edge\" d=\"M%s,%s Q%s,%s %s,%s\" stroke-width=\"%s\" marker-end=\"url(#%s-arrow)\"><title>%s → %s: %s</title></path>"
                       (f sx) (f sy) (f mx) (f my) (f ex) (f ey) (f width) id (escape e.From) (escape e.To) (escape (fmt e.Amount)))
                // label sits on the curve's midpoint
                let lx, ly = 0.25 * sx + 0.5 * mx + 0.25 * ex, 0.25 * sy + 0.5 * my + 0.25 * ey
                let text = fmt e.Amount
                let tw = float text.Length * 7.2 + 12.0
                labels.Append(
                    sprintf "<g class=\"eu-label\"><rect x=\"%s\" y=\"%s\" width=\"%s\" height=\"20\" rx=\"10\"/><text x=\"%s\" y=\"%s\" text-anchor=\"middle\">%s</text></g>"
                        (f (lx - tw / 2.0)) (f (ly - 10.0)) (f tw) (f lx) (f (ly + 4.0)) (escape text)) |> ignore
            | _ -> ()
        w (labels.ToString())
        for p, balance in people do
            let x, y = pos[p]
            let cls = if balance > 0L then "eu-node eu-pos" elif balance < 0L then "eu-node eu-neg" else "eu-node eu-zero"
            // name and balance go outward from the centre so they never sit on edges
            let ox, oy = if n = 1 then 0.0, 1.0 else (x - cx) / radius, (y - cy) / radius
            let tx, ty = x + ox * (nodeR + 20.0), y + oy * (nodeR + 20.0)
            let anchor = if abs ox < 0.3 then "middle" elif ox > 0.0 then "start" else "end"
            let nameY, amountY = if oy < -0.3 then ty - 12.0, ty + 4.0 else ty + 2.0, ty + 18.0
            w (sprintf "<g class=\"%s\"><circle cx=\"%s\" cy=\"%s\" r=\"%s\"/><text class=\"eu-initial\" x=\"%s\" y=\"%s\" text-anchor=\"middle\">%s</text>"
                   cls (f x) (f y) (f nodeR) (f x) (f (y + 6.0)) (escape (initial p)))
            w (sprintf "<text class=\"eu-name\" x=\"%s\" y=\"%s\" text-anchor=\"%s\">%s</text>" (f tx) (f nameY) anchor (escape p))
            let shown = if balance > 0L then "+" + fmt balance else fmt balance
            w (sprintf "<text class=\"eu-bal\" x=\"%s\" y=\"%s\" text-anchor=\"%s\">%s</text></g>" (f tx) (f amountY) anchor (escape shown))
        w "</svg>"
        sb.ToString()

    /// Shared stylesheet for the graph, used by both the report and the playground.
    let css =
        """
.eu-graph{width:100%;height:auto;display:block;font-family:var(--font-ui);overflow:visible}
.eu-edge{fill:none;stroke:var(--edge);stroke-linecap:round;opacity:.85}
.eu-arrow{fill:var(--edge)}
.eu-label rect{fill:var(--surface);stroke:var(--line)}
.eu-label text{font:600 11.5px var(--font-mono);fill:var(--ink);font-variant-numeric:tabular-nums}
.eu-node circle{stroke-width:2.5}
.eu-pos circle{fill:var(--pos-soft);stroke:var(--pos)}
.eu-neg circle{fill:var(--neg-soft);stroke:var(--neg)}
.eu-zero circle{fill:var(--surface);stroke:var(--line)}
.eu-initial{font:700 17px var(--font-ui);fill:var(--ink)}
.eu-name{font:600 13.5px var(--font-ui);fill:var(--ink)}
.eu-bal{font:500 12px var(--font-mono);fill:var(--muted);font-variant-numeric:tabular-nums}
.eu-pos .eu-bal{fill:var(--pos)}
.eu-neg .eu-bal{fill:var(--neg)}
"""
