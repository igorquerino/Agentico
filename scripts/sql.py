import json, re, sys, urllib.request
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8")
# a configuração local da API, relativa a este arquivo (scripts/ -> BlueOS.Api/), e não a um caminho desta máquina
CONFIG = Path(__file__).resolve().parent.parent / "BlueOS.Api" / "appsettings.Local.json"
cfg = json.load(open(CONFIG, encoding="utf-8-sig"))
tok = cfg["Supabase"]["AccessToken"]
REF = "yurhgwqilbgtkvoymyab"

PROD = "oaeqvfmvjahmlgdhpsrv"


class ErroSql(RuntimeError):
    """A Management API recusou o SQL (HTTP de erro): quem chama para, em vez de seguir com um dict no lugar das linhas."""


def so_leitura(sql):
    """A PRODUÇÃO É SÓ LIDA, e isso está no código, não na disciplina: só um SELECT (ou WITH ... SELECT), e embrulhado
    numa subconsulta, onde um segundo statement (;) e um WITH que escreve (insert/update/delete) viram erro de sintaxe."""
    sem_comentario = re.sub(r"/\*.*?\*/", " ", re.sub(r"--[^\n]*", " ", sql), flags=re.S).strip()
    if not re.match(r"(?is)^(select|with)\b", sem_comentario):
        raise ErroSql("a produção é só lida: só SELECT (ou WITH ... SELECT) com ref=PROD.")
    return f"select * from (\n{sql.strip().rstrip(';')}\n) as somente_leitura"


def q(sql, ref=None):
    alvo = ref or REF
    if alvo == PROD: sql = so_leitura(sql)
    req = urllib.request.Request(f"https://api.supabase.com/v1/projects/{alvo}/database/query", method="POST",
                                 data=json.dumps({"query": sql}).encode(),
                                 headers={"Authorization": "Bearer " + tok, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        raise ErroSql(f"HTTP {e.code}: {e.read().decode()[:800]}") from None

if __name__ == "__main__":
    sql = sys.argv[1] if len(sys.argv) > 1 else sys.stdin.read()
    try:
        print(json.dumps(q(sql), ensure_ascii=False, indent=1)[:6000])
    except ErroSql as e:
        print(json.dumps({"erro": str(e)}, ensure_ascii=False)); sys.exit(1)
