import json, sys, urllib.request
sys.stdout.reconfigure(encoding="utf-8")
cfg = json.load(open(r"D:\Bluedesktop\BlueDesk\BlueOS.Api\appsettings.Local.json", encoding="utf-8-sig"))
tok = cfg["Supabase"]["AccessToken"]
REF = "yurhgwqilbgtkvoymyab"

PROD = "oaeqvfmvjahmlgdhpsrv"

def q(sql, ref=None):
    req = urllib.request.Request(f"https://api.supabase.com/v1/projects/{ref or REF}/database/query", method="POST",
                                 data=json.dumps({"query": sql}).encode(),
                                 headers={"Authorization": "Bearer " + tok, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        return {"erro": e.code, "corpo": e.read().decode()[:800]}

if __name__ == "__main__":
    sql = sys.argv[1] if len(sys.argv) > 1 else sys.stdin.read()
    print(json.dumps(q(sql), ensure_ascii=False, indent=1)[:6000])
