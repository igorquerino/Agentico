using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Copy;
using BlueOS.Core.Design;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O AGENTE DESIGNER NA API: a porta de /api/designer/gerar do V1 (o modo da
   casa, com o texto em camada), com banco, modelo de imagem e Storage.
   ----------------------------------------------------------------------------
   POR PEÇA E POR FORMATO (como o V1, uma chamada por peça):
     1. a copy da peça (copy_peca, só estático) e a marca (cliente_brand, logo);
     2. o TEMA da peça: um modelo de texto lê a copy e o cliente e devolve o
        assunto, a cena e o objeto (até 3 leituras; objeto proibido ou tema que
        cita a copy é descartado: peça sem tema é melhor que peça que não sai);
     3. o PROMPT da casa (PromptDaArte.Montar), SEM TEXTO, com a cena do rodízio;
     4. a CENA sai do modelo de imagem (ImagemIa) e vai ao Storage sem texto;
     5. o TEXTO entra como camada (Diagramacao + Compositor, Poppins) e a peça
        final vai ao Storage; `texto_layout` guarda as camadas;
     6. `designer_pecas` (upsert por lote, funil, número e formato), `ia_uso`.
   O LOTE (`designer_lotes`) abre na primeira chamada e fecha com `encerrar`.

   O que NÃO está aqui (modo "livre" do V1, a extensão do feed para story, a
   referência da logo e das telas, a diagramação por visão): a story é gerada
   de novo em 9:16 e a logo entra depois, no estúdio, como no V1.
   ============================================================================ */
public sealed record PedidoDeArte(List<string> PecaIds, List<string> Formatos, string? Template, string? DesignerLoteId, bool Encerrar, string? Ajuste);

public sealed class Designer(Db db, Ia ia, ImagemIa imagem, SupabaseStorage storage, Registro registro, ILogger<Designer> log)
{
    public const string Bucket = "designer-files";
    public const string BucketDaMarca = "brand-assets";

    sealed record CopyDoDesigner(string PecaId, int Ordem, string Funil, string Headline, string Corpo, string Cta, string LoteId, int LoteNumero, bool Aprovada);

    public async Task<(int Status, object Corpo)> GerarAsync(Cliente c, PedidoDeArte p, Guid por)
    {
        var formatos = (p.Formatos ?? []).Select(Diagramacao.Normalizar).Distinct().ToList();
        if (formatos.Count == 0) formatos.Add("feed");
        var template = string.IsNullOrWhiteSpace(p.Template) ? TemplatesDoDesigner.Auto : p.Template.Trim();

        /* ---- encerrar a rodada: o desfecho do lote ---------------------------------- */
        if (p.Encerrar && !string.IsNullOrWhiteSpace(p.DesignerLoteId))
        {
            var fim = await EncerrarAsync(p.DesignerLoteId, c);
            return (200, new { ok = true, lote_id = p.DesignerLoteId, status = fim });
        }
        if (imagem.Provedor is null) return (503, new { error = "sem modelo de imagem na API: informe OPENAI_API_KEY (o provedor do V1) ou GOOGLE_AI_API_KEY em Segredos, no ambiente ou num .env listado em Segredos:Arquivos." });
        var ids = (p.PecaIds ?? []).Where(x => Guid.TryParse(x, out _)).Distinct().ToList();
        if (ids.Count == 0) return (400, new { error = "escolha pelo menos uma peça de copy: o Designer desenha em cima do texto dela." });

        var copies = await CopiesAsync(c, ids);
        if (copies.Count == 0) return (404, new { error = "as peças escolhidas não são deste cliente, ou não são estáticas." });
        var marca = await MarcaAsync(c);
        var squadId = await SquadIdAsync(c);
        var inicio = DateTime.UtcNow;

        /* ---- o lote: abre na primeira chamada, ou reaproveita o informado ------------ */
        var loteDeCopy = copies[0];
        string loteId;
        if (!string.IsNullOrWhiteSpace(p.DesignerLoteId) && Guid.TryParse(p.DesignerLoteId, out var lg))
        {
            var existe = await db.UmAsync(Sql.Fmt($"select id::text as id from public.designer_lotes where id = {lg} and cliente_id = {Guid.Parse(c.Id)}"), r => new { Id = r.S("id") });
            if (existe is null) return (404, new { error = "o lote do Designer informado não é deste cliente." });
            loteId = existe.Id!;
        }
        else
        {
            loteId = (await db.ListaAsync(Sql.Fmt($"""
                insert into public.designer_lotes (squad_id, cliente_id, user_id, numero, copy_lote_id, modelo_ia, fonte, status, pecas_esperadas, template_id)
                values ({(squadId is null ? (Guid?)null : Guid.Parse(squadId))}, {Guid.Parse(c.Id)}, {por}, {loteDeCopy.LoteNumero}, {Guid.Parse(loteDeCopy.LoteId)}, {$"{imagem.Provedor}:{imagem.Modelo}"}, 'gerado', 'gerando', {copies.Count * formatos.Count}, {(template == TemplatesDoDesigner.Auto ? null : template)})
                returning id::text as id
                """), r => r.S("id"))).FirstOrDefault() ?? throw new InvalidOperationException("não consegui abrir o lote do Designer.");
        }

        var contexto = PromptDaArte.ContextoVisual(c);
        var jaUsados = await ObjetosDoLoteAsync(loteId);
        var pecas = new List<object>(); var falhas = new List<object>(); var geradas = 0;
        foreach (var copy in copies)
        {
            var cd = new CopyDaArte(copy.Ordem, copy.Funil, copy.Headline, copy.Corpo, copy.Cta);
            var cena = PromptDaArte.CenaDaPeca(copy.Ordem, copy.Funil, copy.LoteNumero, template == TemplatesDoDesigner.Livre || template == "centralizado" ? null : template);
            var tema = await TemaAsync(c, por, cd, contexto, p.Ajuste, jaUsados);
            if (tema is not null && !jaUsados.Contains(tema.Objeto)) jaUsados.Add(tema.Objeto);
            foreach (var formato in formatos)
            {
                try
                {
                    var r = await PecaAsync(c, por, copy, cd, marca, squadId, loteId, formato, cena, tema, p.Ajuste, template);
                    pecas.Add(r); geradas++;
                }
                catch (Exception e)
                {
                    log.LogWarning("a peça {Funil} {Numero} {Formato} falhou: {Erro}", copy.Funil, copy.Ordem, formato, e.Message);
                    falhas.Add(new { funil = copy.Funil, numero = copy.Ordem, formato, motivo = e.Message });
                    await UsoAsync(c, por, "gerar-peca", "erro", e.Message, 0, 0, 0, 0, imagem.Provedor ?? "imagem", imagem.Modelo);
                }
            }
        }
        await registro.TrilhaAsync("gerou_arte", por, c.Slug, geradas > 0 ? "ok" : "erro", geradas > 0 ? null : "nenhuma arte saiu", "designer_lote", loteId,
            new { via = "api", provedor = imagem.Provedor, modelo = imagem.Modelo, geradas, falhas = falhas.Count, formatos, template }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
        return (geradas > 0 ? 200 : 502, new { ok = geradas > 0, lote_id = loteId, geradas, imagens_cobradas = geradas, provedor = imagem.Provedor, modelo = imagem.Modelo, pecas, falhas });
    }

    /* ---- uma peça num formato ------------------------------------------------------ */

    async Task<object> PecaAsync(Cliente c, Guid por, CopyDoDesigner copy, CopyDaArte cd, MarcaDaArte? marca, string? squadId, string loteId, string formato, string cena, TemaDaPeca? tema, string? ajuste, string template)
    {
        var inicio = DateTime.UtcNow;
        var prompt = PromptDaArte.Montar(cd, c, marca, formato, cena, tema, ajuste);
        var gerada = await imagem.GerarAsync(prompt, formato);
        var (w, h) = Diagramacao.Dimensoes(formato);

        /* a camada de texto: branco sobre o plano de contraste; o destaque e o botão na cor da marca */
        var cores = marca?.Cores.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var corDestaque = cores.FirstOrDefault(Clara) ?? cores.FirstOrDefault() ?? "#7dd3fc";
        var corBotao = !string.IsNullOrWhiteSpace(marca?.CorCta) ? marca!.CorCta! : corDestaque;
        var corTextoBotao = Clara(corBotao) ? "#0b1220" : "#ffffff";
        var layout = Diagramacao.Centralizado(formato, cd.Head, cd.Sub, cd.Cta, "#ffffff", corDestaque, corBotao, corTextoBotao, "esquerda", Diagramacao.DestaquesDaHeadline(cd.Head));
        using var semTexto = Compositor.Enquadrar(gerada.Png, w, h);
        byte[] pngSemTexto;
        using (var img = SkiaSharp.SKImage.FromBitmap(semTexto)) using (var d = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)) pngSemTexto = d.ToArray();
        var pngFinal = Compositor.Compor(gerada.Png, layout, out layout);

        /* o Storage: a cena limpa no caminho da peça, a final em -v{n}-texto.png (como o V1) */
        var versao = 1 + (await db.ListaAsync(Sql.Fmt($"select coalesce(max(versao), 0) as v from public.designer_pecas where lote_id = {Guid.Parse(loteId)} and funil = {copy.Funil.ToUpperInvariant()} and numero = {copy.Ordem} and formato = {formato}"), r => (int)r.L("v"))).FirstOrDefault();
        var caminho = PromptDaArte.CaminhoNoStorage(squadId, c.Slug, copy.LoteNumero, loteId, copy.Funil, copy.Ordem, formato);
        var caminhoFinal = caminho[..^4] + $"-v{versao}-texto.png";
        await storage.SubirAsync(Bucket, caminho, pngSemTexto, "image/png");
        await storage.SubirAsync(Bucket, caminhoFinal, pngFinal, "image/png");
        var url = storage.UrlPublica(Bucket, caminhoFinal);
        var textoLayout = PromptDaArte.TextoLayout(layout, corDestaque, corBotao);

        var id = (await db.ListaAsync(Sql.Fmt($"""
            insert into public.designer_pecas (lote_id, numero, funil, formato, tipo, copy_peca_id, storage_path, storage_url, prompt, modelo_sugerido, template_id, cena_id, status, completed_at, arte_sem_texto, texto_layout, versao)
            values ({Guid.Parse(loteId)}, {copy.Ordem}, {copy.Funil.ToUpperInvariant()}, {formato}, 'estatico', {Guid.Parse(copy.PecaId)}, {caminhoFinal}, {url}, {prompt}, {$"{gerada.Provedor}:{gerada.Modelo}"}, {(template == TemplatesDoDesigner.Auto ? "auto" : template)}, {cena}, 'pronta', now(), {caminho}, {new Jsonb(textoLayout)}, {versao})
            on conflict (lote_id, funil, numero, formato) do update set copy_peca_id = excluded.copy_peca_id, storage_path = excluded.storage_path, storage_url = excluded.storage_url, prompt = excluded.prompt,
              modelo_sugerido = excluded.modelo_sugerido, template_id = excluded.template_id, cena_id = excluded.cena_id, status = 'pronta', completed_at = now(), error = null, arte_sem_texto = excluded.arte_sem_texto, texto_layout = excluded.texto_layout, versao = excluded.versao
            returning id::text as id
            """), r => r.S("id"))).FirstOrDefault() ?? throw new InvalidOperationException("a peça não gravou em designer_pecas.");
        var ms = (int)(DateTime.UtcNow - inicio).TotalMilliseconds;
        await UsoAsync(c, por, "gerar-peca", "ok", null, ms, 0, 0, 1, gerada.Provedor, gerada.Modelo);
        return new { id, funil = copy.Funil.ToUpperInvariant(), numero = copy.Ordem, formato, url = $"{url}?v={DateTime.UtcNow.Ticks}", copy_peca_id = copy.PecaId, cena, tema = tema is null ? null : new { tema.Tema, tema.Objeto, tema.Cena }, provedor = gerada.Provedor, modelo = gerada.Modelo, ms };
    }

    static bool Clara(string hex)
    {
        if (!SkiaSharp.SKColor.TryParse(hex, out var c)) return false;
        return (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255 > 0.55;
    }

    /* ---- o tema da peça (tema-da-peca.ts) --------------------------------------------- */

    async Task<TemaDaPeca?> TemaAsync(Cliente c, Guid por, CopyDaArte copy, string? contexto, string? ajuste, IReadOnlyList<string> jaUsados)
    {
        if (!ia.Configurada || string.IsNullOrWhiteSpace(copy.Head)) return null;
        var descartados = new List<string>();
        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            RespostaDaIa r;
            try { r = await ia.PedirJsonAsync([PromptDaArte.InstrucaoDoTema], PromptDaArte.PedidoDoTema(c.Nome, contexto, copy, ajuste, jaUsados, descartados), PromptDaArte.SchemaDoTema(), 600, "ajuste"); }
            catch (Exception e) { log.LogWarning("tema da peça falhou -> segue sem tema: {Erro}", e.Message); return null; }
            await UsoAsync(c, por, "tema-da-peca", "ok", null, r.LatenciaMs, r.TokensIn, r.TokensOut, 0, "anthropic", r.Modelo);
            var tema = PromptDaArte.LerTema(r.Texto);
            if (tema is null) return null;
            var proibido = PromptDaArte.ObjetoProibido(tema.Objeto, contexto);
            if (proibido is null) return PromptDaArte.CitaCopy(tema, [copy.Head, copy.Sub, copy.Cta]) ? null : tema;
            descartados.Add($"- {tema.Objeto} — cai na regra ({proibido})");
            log.LogInformation("tema da peça: objeto descartado ({Motivo})", proibido);
        }
        return null;
    }

    /* ---- as leituras ---------------------------------------------------------------- */

    async Task<List<CopyDoDesigner>> CopiesAsync(Cliente c, IReadOnlyList<string> ids)
    {
        var guids = ids.Select(Guid.Parse).ToArray();
        return await db.ListaAsync(Sql.Fmt($"""
            select p.peca_id::text as peca_id, p.ordem, i.funil::text as funil, p.headline, p.corpo, p.cta, p.aprovada, l.lote_id::text as lote_id, coalesce(l.numero, 1) as numero
            from public.copy_peca p join public.copy_lote_item i on i.item_id = p.item_id join public.copy_lote l on l.lote_id = i.lote_id
            where p.peca_id = any({guids}) and l.cliente_id = {Guid.Parse(c.Id)} and i.tipo = 'estatico'
            order by l.numero, i.funil, p.ordem
            """), r => new CopyDoDesigner(r.S("peca_id")!, (int)r.L("ordem"), PromptDoCopy.EtapaDoFunil(r.S("funil") ?? "mofu"), r.S("headline") ?? "", r.S("corpo") ?? "", r.S("cta") ?? "", r.S("lote_id")!, (int)r.L("numero"), r.B("aprovada") ?? false));
    }

    async Task<MarcaDaArte?> MarcaAsync(Cliente c)
    {
        var b = await db.UmAsync(Sql.Fmt($"select cores, fonte, observacoes from public.cliente_brand where cliente_id = {Guid.Parse(c.Id)}"), r => new { Cores = r.Textos("cores"), Fonte = r.S("fonte"), Notas = r.S("observacoes") });
        var cores = b?.Cores ?? [];
        if (cores.Count == 0)
        {
            /* sem paleta cadastrada, a cor dominante da logo principal (bucket privado, pela service_role) */
            try
            {
                var logo = await db.UmAsync(Sql.Fmt($"select storage_path from public.cliente_asset where cliente_id = {Guid.Parse(c.Id)} and tipo = 'logo_principal' and em_uso order by criado_em desc limit 1"), r => new { Path = r.S("storage_path") });
                if (logo?.Path is { } path) cores = Compositor.PaletaDaImagem(await storage.BaixarAsync(BucketDaMarca, path), 3);
            }
            catch (Exception e) { log.LogWarning("sem paleta da logo: {Erro}", e.Message); }
        }
        return b is null && cores.Count == 0 ? null : new MarcaDaArte(cores, b?.Fonte, null, [], b?.Notas);
    }

    async Task<string?> SquadIdAsync(Cliente c)
    {
        var l = await db.ListaAsync(Sql.Fmt($"""
            select s.squad_id::text as id from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id
            where cs.cliente_id = {Guid.Parse(c.Id)} and cs.cliente_squad_ate is null order by (cs.cliente_squad_tipo = 'principal') desc, cs.cliente_squad_desde desc limit 1
            """), r => r.S("id"));
        if (l.Count > 0) return l[0];
        var porNome = await db.ListaAsync(Sql.Fmt($"select squad_id::text as id from public.squads where upper(trim(squad_nome)) = {(c.Squad ?? "").Trim().ToUpperInvariant()} limit 1"), r => r.S("id"));
        return porNome.FirstOrDefault();
    }

    async Task<List<string>> ObjetosDoLoteAsync(string loteId)
    {
        try
        {
            var prompts = await db.ListaAsync(Sql.Fmt($"select prompt from public.designer_pecas where lote_id = {Guid.Parse(loteId)} and prompt is not null"), r => r.S("prompt"));
            return PromptDaArte.ObjetosDoLote(prompts);
        }
        catch { return new(); }
    }

    async Task<string> EncerrarAsync(string loteId, Cliente c)
    {
        var n = await db.ListaAsync(Sql.Fmt($"select count(*) filter (where status = 'pronta') as prontas, (select pecas_esperadas from public.designer_lotes where id = {Guid.Parse(loteId)}) as esperadas from public.designer_pecas where lote_id = {Guid.Parse(loteId)}"),
            r => (Prontas: (int)r.L("prontas"), Esperadas: (int)r.L("esperadas")));
        var (prontas, esperadas) = n.FirstOrDefault();
        var status = prontas == 0 ? "falha" : prontas < esperadas ? "parado" : "pronto";
        var aviso = status == "pronto" ? null : $"{prontas} de {esperadas} peça(s) prontas";
        await db.ExecutarAsync(Sql.Fmt($"update public.designer_lotes set status = {status}, aviso = {aviso}, finalizado_em = now() where id = {Guid.Parse(loteId)} and cliente_id = {Guid.Parse(c.Id)}"));
        return status;
    }

    async Task UsoAsync(Cliente c, Guid por, string operacao, string status, string? erro, int ms, int tokensIn, int tokensOut, int imagens, string provedor, string modelo)
    {
        try
        {
            var custo = provedor == "anthropic" ? CopyAgente.CustoUsd(modelo, tokensIn, tokensOut) : imagens * (provedor == "openai" ? 0.19m : 0.04m);
            await db.ExecutarAsync(Sql.Fmt($"insert into public.ia_uso (user_id, cliente_id, agente, operacao, modelo, status, erro, latencia_ms, tokens_in, tokens_out, imagens, custo_usd, ambiente, provedor) values ({por}, {Guid.Parse(c.Id)}, 'designer', {operacao}, {modelo}, {status}, {erro}, {ms}, {tokensIn}, {tokensOut}, {imagens}, {custo}, {db.Ref}, {provedor})"));
        }
        catch (Exception e) { log.LogWarning("ia_uso não gravou: {Erro}", e.Message); }
    }
}
