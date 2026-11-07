"""Sincroniza a HOMOLOGAÇÃO com a PRODUÇÃO (clientes, trafego_contas, cliente_squads, cliente_estrategia,
trafego_integracoes). A produção é SÓ LIDA (select, e o sql.py recusa o resto); toda escrita vai para a homologação;
nada é apagado. A produção vence nas linhas que diferem; os ids de cliente/squad da homologação são preservados
(mapeados por slug/nome).
O MOTOR DA HOMOLOGAÇÃO NÃO VEM DA PRODUÇÃO: motor_modo, motor_automacao* e motor_campanha_id ficam como estão na
homologação, e a conta nova nasce em 'recomenda', com a automação nula. Os tokens são os reais: copiar o 'executa' da
produção faria o cron da homologação virar um segundo motor sobre a mesma conta de anúncio.
O relatório (sync_relatorio.json, ignorado pelo repositório) guarda tabela, chave e erro; nunca o statement, que leva
token e cadastro."""
import json, sys, time
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8")
from sql import q, PROD, REF

AQUI = Path(__file__).resolve().parent
assert REF == "yurhgwqilbgtkvoymyab" and PROD == "oaeqvfmvjahmlgdhpsrv"
tipos = json.load(open(AQUI / "tipos_stag.json", encoding="utf-8"))
relatorio = {"ok": {}, "falhas": {}}
# as colunas do motor em trafego_contas: nunca copiadas da produção
MOTOR = ("motor_modo", "motor_campanha_id", "motor_automacao", "motor_automacao_motivo", "motor_automacao_por", "motor_automacao_em")

def lit(v, tipo):
    if v is None: return "null"
    if tipo == "_text":
        return "'{}'::text[]" if not v else "array[" + ",".join("'" + str(x).replace("'", "''") + "'" for x in v) + "]::text[]"
    if tipo in ("jsonb", "json"):
        if isinstance(v, str):
            # a Management API devolve um jsonb-string como texto puro: precisa voltar a ser JSON ("...")
            try: json.loads(v); s = v
            except ValueError: s = json.dumps(v, ensure_ascii=False)
        else: s = json.dumps(v, ensure_ascii=False)
    elif tipo == "bool": return "true" if v else "false"
    else: s = str(v)
    return "'" + s.replace("'", "''") + "'::" + tipo

def exec_stag(sql):
    return q(sql)                    # homologação; o sql.py levanta ErroSql no erro HTTP

def rodar(tabela, stmts):
    """stmts: [(chave, sql)]. A falha registra a tabela e a CHAVE (slug, id, squad), nunca o statement."""
    ok = 0; falhas = []
    for i in range(0, len(stmts), 40):
        lote = stmts[i:i + 40]
        try: exec_stag(";\n".join(s for _, s in lote)); ok += len(lote)
        except Exception:
            for chave, s in lote:
                try: exec_stag(s); ok += 1
                except Exception as e: falhas.append({"chave": str(chave), "erro": str(e)[:200]})
    relatorio["ok"][tabela] = ok; relatorio["falhas"][tabela] = falhas
    print(f"{tabela}: {ok} statement(s) ok, {len(falhas)} falha(s)")
    for f in falhas[:5]: print("   falha:", f)

def linhas(tabela, ref=None): return q(f"select * from {tabela}", ref)

# ---- 1. clientes: por slug; o cliente_id da homologação fica ----------------------------------------
P = linhas("clientes", PROD); S = linhas("clientes")
t = tipos["clientes"]; cols = [c for c in t if c not in ("cliente_id", "cliente_slug")]
s_por_slug = {r["cliente_slug"]: r for r in S}
stmts = []
for r in P:
    slug = r["cliente_slug"]
    if slug in s_por_slug:
        if all(s_por_slug[slug].get(c) == r.get(c) for c in cols): continue
        stmts.append((slug, f"update public.clientes set " + ", ".join(f"{c} = {lit(r[c], t[c])}" for c in cols) + f" where cliente_slug = {lit(slug, 'text')}"))
    else:
        todas = list(t)
        stmts.append((slug, f"insert into public.clientes ({', '.join(todas)}) values ({', '.join(lit(r[c], t[c]) for c in todas)})"))
print(f"clientes: {len(stmts)} a aplicar"); rodar("clientes", stmts)

# mapas depois do passo 1
S = linhas("clientes")
stag_cid = {r["cliente_slug"]: r["cliente_id"] for r in S}
prod_slug = {r["cliente_id"]: r["cliente_slug"] for r in P}

# ---- 2. trafego_contas: por slug (upsert) ----------------------------------------------------------
P2 = linhas("trafego_contas", PROD); S2 = {r["cliente_slug"]: r for r in linhas("trafego_contas")}
t = tipos["trafego_contas"]; todas = list(t); cols = [c for c in todas if c != "cliente_slug" and c not in MOTOR]
def valor_novo(r, c):
    # a conta que nasce na homologação começa em 'recomenda', sem automação (nula = o padrão); o resto vem da produção
    if c == "motor_modo": return "'recomenda'"
    if c in ("motor_automacao", "motor_automacao_motivo", "motor_automacao_por", "motor_automacao_em"): return "null"
    return lit(r[c], t[c])
stmts = []
for r in P2:
    s = S2.get(r["cliente_slug"])
    if s and all(s.get(c) == r.get(c) for c in cols): continue
    stmts.append((r["cliente_slug"], f"insert into public.trafego_contas ({', '.join(todas)}) values ({', '.join(valor_novo(r, c) for c in todas)}) on conflict (cliente_slug) do update set " + ", ".join(f"{c} = excluded.{c}" for c in cols)))
print(f"trafego_contas: {len(stmts)} a aplicar"); rodar("trafego_contas", stmts)

# ---- 3. cliente_squads: ids de cliente e squad mapeados; sem duplicar vínculo vigente -------------
sq_p = {r["squad_id"]: r["squad_nome"] for r in q("select squad_id, squad_nome from squads", PROD)}
sq_s = {r["squad_nome"]: r["squad_id"] for r in q("select squad_id, squad_nome from squads")}
P3 = linhas("cliente_squads", PROD); S3 = linhas("cliente_squads")
s_por_id = {r["cliente_squad_id"]: r for r in S3}
vig_stag = {}
for r in S3:
    if r["cliente_squad_ate"] is None: vig_stag[(r["cliente_id"], r["squad_id"])] = r["cliente_squad_id"]
t = tipos["cliente_squads"]; outras = [c for c in t if c not in ("cliente_squad_id", "cliente_id", "squad_id")]
# o squad PRINCIPAL vigente é único por cliente (uq_cliente_squad_principal): o da produção vence, e o da homologação é ENCERRADO (ate = hoje), não apagado
principal_stag = {r["cliente_id"]: r["cliente_squad_id"] for r in S3 if r["cliente_squad_tipo"] == "principal" and r["cliente_squad_ate"] is None}
stmts = []; pulados = 0; encerrados = 0
for r in P3:
    slug = prod_slug.get(r["cliente_id"]); cid = stag_cid.get(slug) if slug else None; sid = sq_s.get(sq_p.get(r["squad_id"]))
    if cid is None or sid is None: pulados += 1; continue
    alvo = r["cliente_squad_id"] if r["cliente_squad_id"] in s_por_id else (vig_stag.get((cid, sid)) if r["cliente_squad_ate"] is None else None)
    if r["cliente_squad_tipo"] == "principal" and r["cliente_squad_ate"] is None and principal_stag.get(cid) not in (None, alvo):
        stmts.append((principal_stag[cid], f"update public.cliente_squads set cliente_squad_ate = current_date where cliente_squad_id = {lit(principal_stag[cid], 'uuid')} and cliente_squad_ate is null"))
        principal_stag[cid] = alvo or r["cliente_squad_id"]; encerrados += 1
    if alvo:
        s = s_por_id[alvo]
        if all(s.get(c) == r.get(c) for c in outras) and s["cliente_id"] == cid and s["squad_id"] == sid: continue
        stmts.append((alvo, f"update public.cliente_squads set cliente_id = {lit(cid, 'uuid')}, squad_id = {lit(sid, 'uuid')}, " + ", ".join(f"{c} = {lit(r[c], t[c])}" for c in outras) + f" where cliente_squad_id = {lit(alvo, 'uuid')}"))
    else:
        stmts.append((r["cliente_squad_id"], f"insert into public.cliente_squads (cliente_squad_id, cliente_id, squad_id, {', '.join(outras)}) values ({lit(r['cliente_squad_id'], 'uuid')}, {lit(cid, 'uuid')}, {lit(sid, 'uuid')}, {', '.join(lit(r[c], t[c]) for c in outras)})"))
print(f"cliente_squads: {len(stmts)} a aplicar, {pulados} pulado(s) por cliente/squad inexistente, {encerrados} principal(is) da homologação encerrado(s)"); rodar("cliente_squads", stmts)

# ---- 4. cliente_estrategia: por cliente (mapeado), upsert --------------------------------------------
P4 = linhas("cliente_estrategia", PROD); S4 = {r["cliente_id"]: r for r in linhas("cliente_estrategia")}
t = tipos["cliente_estrategia"]; outras = [c for c in t if c != "cliente_id"]
stmts = []
for r in P4:
    slug = prod_slug.get(r["cliente_id"]); cid = stag_cid.get(slug) if slug else None
    if cid is None: continue
    s = S4.get(cid)
    if s and all(s.get(c) == r.get(c) for c in outras): continue
    stmts.append((slug, f"insert into public.cliente_estrategia (cliente_id, {', '.join(outras)}) values ({lit(cid, 'uuid')}, {', '.join(lit(r[c], t[c]) for c in outras)}) on conflict (cliente_id) do update set " + ", ".join(f"{c} = excluded.{c}" for c in outras)))
print(f"cliente_estrategia: {len(stmts)} a aplicar"); rodar("cliente_estrategia", stmts)

# ---- 5. trafego_integracoes: insere o squad que falta; atualiza só se a produção for mais nova -------
P5 = linhas("trafego_integracoes", PROD); S5 = {r["squad_nome"]: r for r in linhas("trafego_integracoes")}
t = tipos["trafego_integracoes"]; todas = list(t); outras = [c for c in todas if c != "squad_nome"]
stmts = []; mantidos = []
for r in P5:
    s = S5.get(r["squad_nome"])
    if s and (s["atualizado_em"] or "") >= (r["atualizado_em"] or ""): mantidos.append(r["squad_nome"]); continue
    stmts.append((r["squad_nome"], f"insert into public.trafego_integracoes ({', '.join(todas)}) values ({', '.join(lit(r[c], t[c]) for c in todas)}) on conflict (squad_nome) do update set " + ", ".join(f"{c} = excluded.{c}" for c in outras)))
print(f"trafego_integracoes: {len(stmts)} a aplicar ({[r['squad_nome'] for r in P5 if r['squad_nome'] not in mantidos]}); mantidos da homologação (mais novos ou iguais): {mantidos}")
rodar("trafego_integracoes", stmts)

json.dump(relatorio, open(AQUI / "sync_relatorio.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("\nRESUMO:", {k: relatorio["ok"][k] for k in relatorio["ok"]}, "| falhas:", {k: len(v) for k, v in relatorio["falhas"].items()})
