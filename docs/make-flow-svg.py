"""Generates docs/concordia-flow.svg, the animation in the README: python3 docs/make-flow-svg.py docs/concordia-flow.svg"""
import sys

T = 16.0      # loop length in seconds
FADE = 0.18   # fade in/out duration
END = 15.4    # everything fades out here, then the loop restarts

C = dict(bg="#0d1117", panel="#161b22", border="#30363d", text="#e6edf3", muted="#8b949e",
         dim="#484f58", blue="#58a6ff", green="#3fb950", red="#f85149", amber="#d29922", purple="#bc8cff")

css, body = [], []
_n = 0
final = []  # classes visible in the finished state (for reduced motion)


def pct(t):
    return f"{max(0.0, min(100.0, t / T * 100)):.3f}%"


def anim(intervals):
    """CSS class name for an element visible only during the given (start, end) intervals."""
    global _n
    _n += 1
    name = f"a{_n}"
    frames = {0.0: 0}
    for s, e in intervals:
        frames[max(0.0, s - 0.001)] = 0
        frames[s + FADE] = 1
        frames[max(s + FADE, e - FADE)] = 1
        frames[e] = 0
    frames[T] = 0
    kf = " ".join(f"{pct(t)}{{opacity:{v}}}" for t, v in sorted(frames.items()))
    css.append(f"@keyframes {name}{{{kf}}} .{name}{{opacity:0;animation:{name} {T}s linear infinite}}")
    if any(s <= END - 0.5 < e for s, e in intervals):
        final.append(name)
    return name


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


W, H = 960, 500

# ---------------------------------------------------------------- frame
body.append(f'<rect x="0.5" y="0.5" width="{W-1}" height="{H-1}" rx="14" fill="{C["bg"]}" stroke="{C["border"]}"/>')
body.append(f'<text x="32" y="44" class="sans" font-size="20" font-weight="700" fill="{C["text"]}">Concordia</text>')
body.append(f'<text x="146" y="44" class="sans" font-size="14" fill="{C["muted"]}">agent orchestration that proves what it ships</text>')

# ---------------------------------------------------------------- pipeline
stations = [
    ("Request", [(0.2, 1.8)], [], [(1.8, END)]),
    ("Triage", [(1.8, 2.7)], [], [(2.7, END)]),
    ("Tests first", [(2.7, 4.6)], [], [(4.6, END)]),
    ("Implement", [(4.6, 7.2), (8.8, 9.7)], [], [(7.2, 8.8), (9.7, END)]),
    ("Gate", [(7.2, 7.8), (9.7, 10.2)], [(7.8, 9.7)], [(10.2, END)]),
    ("Review", [(10.6, 13.0)], [], [(13.0, END)]),
    ("Done", [], [], [(13.4, END)]),
]
sy = 124
xs = [96 + i * 128 for i in range(len(stations))]

for i in range(len(stations) - 1):
    x1, x2 = xs[i] + 22, xs[i + 1] - 22
    body.append(f'<line x1="{x1}" y1="{sy}" x2="{x2}" y2="{sy}" stroke="{C["dim"]}" stroke-width="2"/>')
    start = stations[i + 1][1][0][0] if stations[i + 1][1] else stations[i + 1][3][0][0]
    body.append(f'<line class="{anim([(start, END)])}" x1="{x1}" y1="{sy}" x2="{x2}" y2="{sy}" stroke="{C["green"]}" stroke-width="2"/>')

# feedback loop: Gate -> Implement while the gate is red
gx, ix = xs[4], xs[3]
loop = f"M{gx} {sy+24} C{gx} {sy+62} {ix} {sy+62} {ix} {sy+24}"
body.append(f'<path d="{loop}" fill="none" stroke="{C["dim"]}" stroke-width="1.5" stroke-dasharray="4 4"/>')
red_loop = anim([(7.8, 9.0)])
body.append(f'<g class="{red_loop}"><path d="{loop}" fill="none" stroke="{C["red"]}" stroke-width="2" marker-end="url(#ar)"/>'
            f'<text x="{(gx+ix)//2}" y="{sy+70}" text-anchor="middle" class="mono" font-size="11" fill="{C["red"]}">errors sent back</text></g>')
dot = "dotmove"
css.append(f"@keyframes {dot}{{0%{{offset-distance:100%;opacity:0}} {pct(7.85)}{{offset-distance:100%;opacity:0}}"
           f" {pct(7.95)}{{opacity:1}} {pct(8.8)}{{offset-distance:0%;opacity:1}} {pct(8.9)}{{offset-distance:0%;opacity:0}} 100%{{offset-distance:0%;opacity:0}}}}"
           f" .{dot}{{offset-path:path('{loop}');animation:{dot} {T}s linear infinite;opacity:0}}")
body.append(f'<circle class="{dot}" r="5" fill="{C["red"]}"/>')

for (label, active, fail, done), x in zip(stations, xs):
    body.append(f'<circle cx="{x}" cy="{sy}" r="20" fill="{C["panel"]}" stroke="{C["dim"]}" stroke-width="2"/>')
    if active:
        a = anim(active)
        body.append(f'<g class="{a}"><circle cx="{x}" cy="{sy}" r="20" fill="{C["blue"]}" fill-opacity="0.18" stroke="{C["blue"]}" stroke-width="2"/>'
                    f'<circle class="pulse" cx="{x}" cy="{sy}" r="20" fill="none" stroke="{C["blue"]}" stroke-width="2"/>'
                    f'<circle cx="{x}" cy="{sy}" r="5" fill="{C["blue"]}"/></g>')
    if fail:
        f = anim(fail)
        body.append(f'<g class="{f}"><circle cx="{x}" cy="{sy}" r="20" fill="{C["red"]}" fill-opacity="0.18" stroke="{C["red"]}" stroke-width="2"/>'
                    f'<path d="M{x-7} {sy-7}L{x+7} {sy+7}M{x+7} {sy-7}L{x-7} {sy+7}" stroke="{C["red"]}" stroke-width="3" stroke-linecap="round"/></g>')
    if done:
        d = anim(done)
        body.append(f'<g class="{d}"><circle cx="{x}" cy="{sy}" r="20" fill="{C["green"]}" fill-opacity="0.18" stroke="{C["green"]}" stroke-width="2"/>'
                    f'<path d="M{x-8} {sy}L{x-2} {sy+6}L{x+9} {sy-6}" fill="none" stroke="{C["green"]}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/></g>')
    body.append(f'<text x="{x}" y="{sy-46}" text-anchor="middle" class="sans" font-size="13" font-weight="600" fill="{C["text"]}">{label}</text>')

# small badges under stations
badges = [
    (xs[1], "S · M · L", C["muted"], [(0, END)]),
    (xs[1], "large", C["blue"], [(2.2, END)]),
    (xs[4], "build · test", C["muted"], [(0, END)]),
    (xs[5], "lenses by rule", C["muted"], [(0, END)]),
]
for x, text, color, iv in [badges[0]] + badges[2:]:
    body.append(f'<text x="{x}" y="{sy-29}" text-anchor="middle" class="mono" font-size="11" fill="{color}">{text}</text>')
cls = anim(badges[1][3])
body.append(f'<g class="{cls}"><rect x="{xs[1]-34}" y="{sy+27}" width="68" height="19" rx="9.5" fill="{C["bg"]}" stroke="{C["blue"]}"/>'
            f'<text x="{xs[1]}" y="{sy+41}" text-anchor="middle" class="mono" font-size="11" fill="{C["blue"]}">large</text></g>')

# ---------------------------------------------------------------- terminal transcript
tx, ty, tw, th = 32, 212, W - 64, 262
body.append(f'<rect x="{tx}" y="{ty}" width="{tw}" height="{th}" rx="10" fill="{C["panel"]}" stroke="{C["border"]}"/>')
for k, col in enumerate([C["red"], C["amber"], C["green"]]):
    body.append(f'<circle cx="{tx+18+k*16}" cy="{ty+16}" r="5" fill="{col}" fill-opacity="0.8"/>')
body.append(f'<text x="{tx+tw/2}" y="{ty+20}" text-anchor="middle" class="mono" font-size="11" fill="{C["muted"]}">claude code session</text>')

# (time, segments) where each segment is (text, color)
lines = [
    (0.3, [("> ", C["muted"]), ("/concordia:feature ", C["purple"]), ("cancel appointments with a 50% late fee", C["text"])]),
    (2.0, [("triage   ", C["muted"]), ("large", C["blue"]), (" — new endpoint, contract fields, status codes", C["text"])]),
    (2.9, [("planner  ", C["muted"]), ("plan written → ", C["text"]), ("approved by you", C["blue"])]),
    (3.8, [("tests    ", C["muted"]), ("test-author wrote 11 tests → ", C["text"]), ("11 failing, as expected", C["red"])]),
    (5.0, [("pre-tool ", C["muted"]), ("edit Migrations/0001_Init.cs → ", C["text"]), ("blocked: committed file is protected", C["amber"])]),
    (6.1, [("edit     ", C["muted"]), ("Domain/Appointment.cs  Endpoints/AppointmentEndpoints.cs", C["text"])]),
    (7.6, [("gate     ", C["muted"]), ("build ✓  ", C["green"]), ("tests ✗ 1 failed", C["red"]), ("  (attempt 1 of 3)", C["muted"])]),
    (8.2, [("           Failed Cancelling_twice_returns_409 → ", C["red"]), ("back to the agent, 2 lines", C["muted"])]),
    (10.0, [("gate     ", C["muted"]), ("build ✓  tests ✓  green", C["green"]), ("  — the agent may finish", C["muted"])]),
    (11.0, [("review   ", C["muted"]), ("review-pack → lenses ", C["text"]), ("security, domain", C["purple"]), ("  (rules, 0 tokens)", C["muted"])]),
    (12.2, [("           2 reviewers in parallel → ", C["text"]), ("1 finding fixed", C["green"]), (", 0 open", C["muted"])]),
    (13.5, [("✓ done   ", C["green"]), ("proven, not claimed", C["text"])]),
]
ly = ty + 48
for t, segs in lines:
    c = anim([(t, END)])
    spans = "".join(f'<tspan fill="{col}">{esc(s)}</tspan>' for s, col in segs)
    body.append(f'<text class="mono {c}" x="{tx+18}" y="{ly}" font-size="13" xml:space="preserve">{spans}</text>')
    ly += 18

# blinking cursor while typing the request
cur = anim([(0.0, 0.6)])
body.append(f'<rect class="{cur}" x="{tx+18}" y="{ty+37}" width="8" height="14" fill="{C["text"]}"/>')

defs = (f'<defs><marker id="ar" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">'
        f'<path d="M0 0L10 5L0 10z" fill="{C["red"]}"/></marker></defs>')

style = (".sans{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif}"
         ".mono{font-family:ui-monospace,SFMono-Regular,'SF Mono',Menlo,Consolas,'Liberation Mono',monospace}"
         "@keyframes pulse{0%{transform:scale(1);opacity:.9}100%{transform:scale(1.6);opacity:0}}"
         ".pulse{transform-box:fill-box;transform-origin:center;animation:pulse 1.1s ease-out infinite}"
         + "".join(css)
         # Reduced motion: show the finished state, no animation.
         + "@media (prefers-reduced-motion:reduce){*{animation:none!important}"
         + "".join(f".{c}{{opacity:1!important}}" for c in final) + ".dotmove,.pulse{display:none}}")

svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" '
       f'aria-label="How Concordia works: triage, tests first, a verification gate that sends failures back to the agent, risk-based review, done.">'
       f'<title>How Concordia works</title><style>{style}</style>{defs}{"".join(body)}</svg>')

open(sys.argv[1], "w", encoding="utf-8").write(svg)
print(f"{len(svg)/1024:.1f} KB, {_n} animated groups")
