# Python port of RulesEngine.PlanFall (11-17 rule) to sanity-check termination + results
def plan(occ, fix, W, H, spawn, past_sealed=True):
    HT = H + spawn
    org = [[None]*HT for _ in range(W)]
    for x in range(W):
        for y in range(HT):
            if y >= H or occ[x][y]: org[x][y] = (x, y)
    def Fixed(x, y): return y < H and org[x][y] is not None and org[x][y][1] < H and fix[org[x][y][0]][org[x][y][1]]
    def Free(x, y): return y < H and org[x][y] is None
    moves = [0]
    def Move(fx, fy, tx, ty): org[tx][ty] = org[fx][fy]; org[fx][fy] = None; moves[0] += 1
    def Pack():
        for x in range(W):
            y0 = 0
            while y0 < HT:
                y1 = y0
                while y1 < HT and not Fixed(x, y1): y1 += 1
                w = y0
                for y in range(y0, y1):
                    if org[x][y] is None: continue
                    if w < y: Move(x, y, x, w)
                    w += 1
                y0 = y1 + 1
    def UnderFixed(x, y):
        for j in range(y+1, HT): return Fixed(x, j)
        return False
    def BlockedAbove(x, y):
        for j in range(y+1, HT):
            if Fixed(x, j): return True
            if org[x][j] is not None: return False
        return False
    def HasSource(x, y):
        sy = y+1
        if sy >= HT: return False
        for sx in (x-1, x+1):
            if 0 <= sx < W and org[sx][sy] is not None and not Fixed(sx, sy): return True
        return False
    def NoSourceAbove(x, y):
        for j in range(y+1, HT):
            if not Free(x, j): break
            if HasSource(x, j): return False
        return True
    Pack(); waves = 0
    for guard in range(W*HT):
        slid = False
        for y in range(H-1):
            for x in range(W):
                if not Free(x, y): continue
                if not (UnderFixed(x, y) or (past_sealed and BlockedAbove(x, y) and NoSourceAbove(x, y))): continue
                first = -1 if ((x+y) & 1) == 0 else 1
                for k in range(2):
                    sx = x + (first if k == 0 else -first); sy = y+1
                    if sx < 0 or sx >= W or org[sx][sy] is None or Fixed(sx, sy): continue
                    Move(sx, sy, x, y); slid = True; break
        if not slid: break
        waves += 1; Pack()
    else:
        print('!! guard exhausted')
    return org, waves, moves[0]
def show(org, W, H, fix):
    for y in range(H-1, -1, -1):
        print(''.join('#' if (org[x][y] is not None and org[x][y][1] < H and fix[org[x][y][0]][org[x][y][1]]) else ('.' if org[x][y] is None else ('n' if org[x][y][1] >= H else 'o')) for x in range(W)))
W, H = 8, 10
def board(holes, crates):
    occ = [[True]*H for _ in range(W)]; fix = [[False]*H for _ in range(W)]
    for (x, y) in crates: fix[x][y] = True
    for (x, y) in holes: occ[x][y] = False
    return occ, fix
crates = [(x, 6) for x in range(1, 7)] + [(x, 5) for x in range(2, 6)]
for name, holes in (('recording: holes (3,4),(4,4),(4,3)', [(3,4),(4,4),(4,3)]), ('whole area under the lid empty', [(x, y) for x in range(2, 6) for y in range(0, 5)]), ('column 4 emptied below the lid', [(4, y) for y in range(0, 5)])):
    for ps in (False, True):
        occ, fix = board(holes, crates)
        org, waves, mv = plan(occ, fix, W, H, 20, ps)
        empties = sum(1 for x in range(W) for y in range(H) if org[x][y] is None)
        print(f'--- {name} | past_sealed={ps}: waves {waves}, steps {mv}, empty cells left {empties}')
        show(org, W, H, fix)
# random stress: must always terminate and never lose/duplicate a piece
import random
random.seed(1)
for trial in range(3000):
    occ = [[random.random() > 0.25 for _ in range(H)] for _ in range(W)]
    fix = [[occ[x][y] and random.random() < 0.2 for y in range(H)] for x in range(W)]
    n0 = sum(occ[x][y] for x in range(W) for y in range(H))
    org, waves, mv = plan(occ, fix, W, H, 20, True)
    seen = [org[x][y] for x in range(W) for y in range(H+20) if org[x][y] is not None]
    assert len(seen) == len(set(seen)), 'duplicate piece'
    assert len(seen) == n0 + W*20, 'piece lost'
    for x in range(W):
        for y in range(H):
            if fix[x][y]: assert org[x][y] == (x, y), 'a fixed piece moved'
print('stress: 3000 random boards OK (terminates, no piece lost or duplicated, fixed pieces never move)')
