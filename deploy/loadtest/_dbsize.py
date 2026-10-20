"""一次性核对：压测后 control.db 各表行数与占用（只读打开，不改文件）。

用法：python _dbsize.py <control.db 路径>
"""
import os
import sqlite3
import sys

path = sys.argv[1]
print("files:")
for suffix in ("", "-wal", "-shm"):
    f = path + suffix
    if os.path.exists(f):
        print("  %-22s %8.2f MB" % (os.path.basename(f), os.path.getsize(f) / 1048576))

uri = "file:" + path.replace("\\", "/") + "?mode=ro"
con = sqlite3.connect(uri, uri=True)
cur = con.cursor()

page_size = cur.execute("pragma page_size").fetchone()[0]
try:
    total = cur.execute("select sum(pgsize) from dbstat").fetchone()[0] or 0
    has_dbstat = True
except sqlite3.OperationalError:
    total = 0
    has_dbstat = False

print("\npage_size=%d  dbstat=%s  total=%.2f MB" % (page_size, has_dbstat, total / 1048576))
print("\n%-28s %10s %10s" % ("table", "rows", "MB"))

tables = [r[0] for r in cur.execute(
    "select name from sqlite_master where type='table' order by name").fetchall()]
for t in tables:
    try:
        rows = cur.execute('select count(*) from "%s"' % t).fetchone()[0]
    except sqlite3.OperationalError as exc:
        print("%-28s  ERR %s" % (t, exc))
        continue
    size = 0
    if has_dbstat:
        size = cur.execute("select sum(pgsize) from dbstat where name=?", (t,)).fetchone()[0] or 0
    print("%-28s %10d %10.3f" % (t, rows, size / 1048576))

con.close()
