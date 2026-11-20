"""Compares two recorded camera paths (poses.jsonl from the Unity and native gameplay recorders).

python pose_drift.py <reference poses.jsonl> <port poses.jsonl> [--every 15]
Prints position/yaw/pitch/FOV per sample, ground speeds, and the first time the paths separate by more than 1 m.
"""
import argparse, json, math

def load(path): return [json.loads(line) for line in open(path)]
def yaw(p): return math.degrees(math.atan2(p['forward'][0], p['forward'][2]))
def pitch(p): return math.degrees(math.asin(max(-1, min(1, p['forward'][1]))))
def flat(a, b): return math.hypot(a['eye'][0] - b['eye'][0], a['eye'][2] - b['eye'][2])
def speed(path, i, span=3):
    if i < span: return 0
    return flat(path[i], path[i - span]) / (path[i]['time'] - path[i - span]['time'])

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('reference'); p.add_argument('port'); p.add_argument('--every', type=int, default=15)
    a = p.parse_args()
    ref, port = load(a.reference), load(a.port)
    n = min(len(ref), len(port)); separated = None
    for i in range(n):
        r, q = ref[i], port[i]
        d = flat(r, q); dy = ((yaw(q) - yaw(r) + 180) % 360) - 180
        if separated is None and d > 1: separated = r['time']
        if i % a.every == 0:
            print(f"t={r['time']:6.2f} apart={d:6.2f}m dy={q['eye'][1]-r['eye'][1]:+5.2f} dyaw={dy:+6.1f} dpitch={pitch(q)-pitch(r):+5.1f} "
                  f"fov {r['fov']:5.1f}/{q['fov']:5.1f} speed {speed(ref,i):5.2f}/{speed(port,i):5.2f}")
    print('paths separate (>1 m) at', 'never' if separated is None else f't={separated:.2f}s')
