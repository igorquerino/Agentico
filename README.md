# Blue OS em C#/.NET: MVP da tela do Gestor de Tráfego (desktop)

Primeiro passo da migração do Blue OS V1 (Next.js) para C#/.NET 10, seguindo o plano em
`D:\Bluedesktop\Blue OS em C# .NET mapeamento e plano de migração.md`: MAUI Blazor Hybrid
para o desktop e Blazor Web App para a web, as duas saindo da mesma biblioteca de telas.
Hoje só o desktop (Windows) e só uma tela: a do cargo **gestor de tráfego**.

## A solução

| Projeto | O que é |
| --- | --- |
| `BlueOS` | O app desktop (MAUI Blazor Hybrid, só `net10.0-windows10.0.19041.0`). Projeto de inicialização. |
| `BlueOS.Shared` | A biblioteca de telas (Razor). É o que o desktop e a web compartilham. |
| `BlueOS.Web` | O host web (Blazor Interactive Server, global). Fica pronto para a versão web; hoje serve para abrir a mesma tela no navegador. |
| `BlueOS.Core` | O domínio, sem UI: modelos, o motor de tráfego portado do V1, o agente de copy, o gerador de artes e as duas fontes de dados. |
| `BlueOS.Tests` | xUnit: planejador, árvore de decisão, régua de MQL, crivo da copy e o fluxo ponta a ponta. |

O projeto `BlueDesk` original (template "MAUI para Aspire Service Defaults", sem app) foi
parado em `D:\Bluedesktop\_template-antigo\` e saiu da solução.

## Rodar

No Visual Studio: abra `BlueDesk.slnx`, defina `BlueOS` como projeto de inicialização,
escolha **Windows Machine** e aperte F5. Pela linha de comando:

```powershell
dotnet build BlueOS\BlueOS.csproj -f net10.0-windows10.0.19041.0
.\BlueOS\bin\Debug\net10.0-windows10.0.19041.0\win-x64\BlueOS.exe

dotnet run --project BlueOS.Web --launch-profile http    # a mesma tela em http://localhost:5235
dotnet test BlueOS.Tests
```

## A tela: um fluxo só, da chegada à automação

A carteira mostra um card por cliente com o **estágio** em que ele está (o primeiro passo
incompleto) e o que falta. Abrir um cliente não muda de tela: o stepper de 7 etapas fica no
topo e cada etapa rende abaixo.

0. **Start automático, pelo card.** O card é vertical, com a logo no centro; quem está em automação (ou
   entrando nela) ganha um feixe de luz girando em volta da logo. O botão **Start** abre um painel no
   próprio card: confirma nicho, budget e a régua de MQL (proposta pelo critério gravado ou pelo
   formulário da Meta, com cada resposta como chip forte/média/eliminatória), escolhe o modo do agente
   (recomenda, sombra ou automático; o automático pede confirmação do que ele faz sozinho) e dispara o
   **condutor** (`Core/Fluxo/Condutor.cs`): grava a ficha, escreve a copy das etapas da faixa e aprova o
   que passa no crivo, desenha um criativo por etapa e põe cada um na vaga certa, publica o Start
   pausado e liga o agente no modo escolhido. O que só gente resolve (logo e assets, conta, política de
   privacidade) para o condutor com "precisa de você: ...". A trilha fica no card.
1. **Chegada**: o cliente entrou.
2. **Assets**: logo principal, logo variação, telas do produto (bucket `brand-assets`), conta
   de anúncios (`act_`), página, token do squad conferido na Meta, lista do cliente, site/pixel,
   política de privacidade e link.
3. **Ficha**: o que trava o fluxo é só o card Tráfego (nicho e budget mensal) e o critério de
   MQL (régua v2 e teto por MQL do PPL). Cadastro, ICP, produto e estratégia continuam na ficha,
   mas são opcionais. A régua se monta a partir do **formulário da Meta**: a API lê os
   formulários de cadastro da página do cliente (token do squad trocado pelo token de página),
   o gestor escolhe o formulário e marca cada resposta como forte, média ou eliminatória; a
   régua grava a chave da resposta como a Meta entrega no lead, igual ao V1.
4. **Copy**: o agente escreve as peças das etapas que a **faixa do budget** pede (f1: só MOFU;
   f2: MOFU e BOFU; f3: TOFU, MOFU e BOFU), com a estrutura declarada (ângulo, papel da sub,
   tipo de CTA) e o crivo em código (régua V8 e regras fechadas do vault). O gestor aprova.
5. **Design**: as artes das peças aprovadas (feed e story) e a escolha dos criativos piloto, **um por
   etapa do funil** que a faixa do budget pede (f1 só MOFU; f2 MOFU e BOFU; f3 TOFU, MOFU e BOFU):
   cada conjunto do Start leva o piloto da etapa dele. O **validador de assento** confere que a arte
   em cada vaga nasceu da copy daquela etapa (MOFU na vaga de MOFU, BOFU na de BOFU): vaga errada é
   pendência do Design e recusa do Publicar (`assento_errado`). O Publicar do V1 ainda sobe um criativo só
   (o de MOFU) para todos os conjuntos; os outros pilotos entram pela troca de criativo. Nas
   O seletor **"Como o criativo sai"** é o de-para do seletor do V1 (`Core/Design/TemplatesDoDesigner.cs`,
   cópia de `lib/designer/estilos-lista.ts`): Auto (rodízio de cena), uma cena fixa (notebook, celular,
   pessoa do ICP, conceito 3D, objeto símbolo, mockup premium, comparativo), Centralizado (padrão da
   casa) ou Gerar livre (sem layout). Só o id viaja em `peca.template`; o prompt mora no V1. Nas
   fontes reais o botão Gerar chama o **Agente Designer do V1** (`POST /api/designer/gerar`):
   uma chamada abre o lote da geração e devolve o plano, depois uma chamada por peça e
   formato gera a imagem com o modelo de imagem (OpenAI gpt-image, `OPENAI_API_KEY` no
   ambiente do V1). Cada arte entra na galeria assim que fica pronta; as já existentes vêm
   da biblioteca do Designer. Os lotes de copy do cliente vêm do V1 (`/api/os/copy/lotes`)
   ou da API .NET, e aprovar uma peça grava em `copy_peca.aprovada`.
6. **Campanha (Start)**: a estrutura proposta pelo planejador (faixa, conjuntos, públicos,
   verba por dia, nomes da Meta, MQL esperado) e o Publicar, tudo PAUSED, com o link do
   Gerenciador.
7. **Automação**: KPIs do mês, o modo do motor (recomenda, sombra, executa), a chave geral,
   as recomendações da árvore com a trilha de regras, o aceite/negativa do gestor, os
   conjuntos em veiculação, o que o agente fez e a central de leads.

Deep link: `/?cliente=<slug>&etapa=automacao` abre direto no cliente e na etapa.

## Fontes de dados

O botão **Fonte de dados** (canto superior direito) troca entre:

- **Demonstração** (padrão): tudo em memória, com a carteira do squad POSEIDON semeada em
  estágios diferentes. A Meta, a carga diária e os agentes de Copy e Design são simulados;
  o planejador, a árvore, a régua e o crivo são os reais.
- **Blue OS V1**: login pelo `/api/os/login` (cookie de sessão) e as rotas `/api/trafego/*`
  e `/api/os/clientes/*` que o V1 já tem. O app nunca guarda chave do banco nem service
  role. Exercitado em 29/10/2026 contra o V1 em `localhost:3080` (banco de homologação) com
  o usuário de QA dos testes e2e do próprio V1: carteira, detalhe do cliente, card Tráfego,
  critério de MQL, assets, execução, estrutura, ongoing, recomendações, peças do Designer e
  token do squad respondem e são lidos. A lista das rotas e dos corpos está no cabeçalho de
  `BlueOS.Core/Api/V1Api.cs`.

  O que fica **só de leitura** no modo V1 por enquanto: produto e estratégia da ficha (sem
  rota de escrita), o financeiro do contrato (de coordenador pra cima) e a geração de arte
  (feita no Agente Designer do V1; as peças chegam pela biblioteca).

  **Conexão automática de desenvolvimento:** com `BLUEOS_V1_URL`, `BLUEOS_V1_LOGIN` e
  `BLUEOS_V1_SENHA` no ambiente do processo, o app abre já ligado ao V1. A senha fica só na
  memória do processo. Exemplo no PowerShell:

  ```powershell
  $env:BLUEOS_V1_URL = "http://localhost:3080"; $env:BLUEOS_V1_LOGIN = "seu_usuario"; $env:BLUEOS_V1_SENHA = "sua_senha"
  .\BlueOS\bin\Debug\net10.0-windows10.0.19041.0\win-x64\BlueOS.exe
  ```

  Subir o V1 no localhost não toca a produção: o `.env.local` aponta para o banco de
  homologação, a escrita na Meta é bloqueada fora de produção e toda rota de cron responde
  404 nesse banco. Exceções conhecidas: salvar o critério de MQL em texto ecoa para o OS
  antigo em produção (o desktop não envia esse campo), e gerar copy gasta crédito da chave
  da Anthropic do `.env.local`.

## O que foi portado do V1 (fiel, com testes)

| V1 (TypeScript) | Aqui (C#) |
| --- | --- |
| `lib/trafego/motor/planejador/faixa.ts` | `Core/Motor/Faixa.cs` |
| `lib/trafego/motor/planejador/planejar.ts` | `Core/Motor/Planejador.cs` |
| `lib/trafego/motor/planejador/estrutura.ts` | `Core/Motor/Estrutura.cs` |
| `lib/trafego/motor/arvore/decidir.ts` + `alerta-budget.ts` | `Core/Motor/Arvore.cs` |
| `lib/trafego/motor/regua/avaliar.ts` | `Core/Motor/Regua.cs` |
| `lib/trafego/motor/contrato.ts` (parâmetros do vault 9715c94) | `Core/Modelos/Motor.cs` |
| `lib/trafego/motor/setup/cadastro.ts` (nichos) e `budget.ts` | `Core/Setup/Nichos.cs`, `Core/Motor/Dinheiro.cs` |
| `lib/copy/estruturas.ts`, `contrato.ts`, régua V8 | `Core/Copy/Estruturas.cs`, `GeradorDemo.cs` (crivo) |

## A API .NET (`BlueOS.Api`, trilha B do plano)

Minimal API em ASP.NET Core com **Npgsql direto no Postgres do Supabase**, um round-trip por
tela (joins e agregações em JSON no SQL). É a porta única para os dados: o RBAC do V1
(`escopo-server.ts`, `trafego/escopo.ts`) vive em `Dominio/Escopo.cs` numa policy só, e a
sessão é a da pessoa (login no Supabase Auth, JWT em cada chamada). O que ainda escreve na
Meta ou no Storage (publicar, rodar o motor, aceite de PAUSAR/DUPLICAR, token, upload,
copy pelo modelo) continua indo ao V1, pela mesma sessão, até o executor e a carga
existirem aqui.

| Rota | O que faz |
| --- | --- |
| `POST /api/login` | usuário ou e-mail + senha; devolve o JWT da pessoa e o escopo |
| `GET /api/carteira` | todos os clientes do escopo com ficha, card Tráfego, critério, assets, publicação, squads e recomendações pendentes, numa consulta |
| `GET /api/clientes/{slug}` | o cliente completo, com as artes do Designer e as recomendações com estado |
| `GET /api/clientes/{slug}/ongoing` | KPIs do mês, conjuntos, leads pela régua e o que o agente fez |
| `GET /api/clientes/{slug}/estrutura` | o Start, calculado pelo planejador portado |
| `GET /api/clientes/{slug}/formularios` | os formulários de cadastro da página do cliente na Meta, com perguntas e opções (tenta o token do squad do cliente, depois os dos outros squads; 422 quando nenhum administra a página) |
| `POST .../trafego`, `POST .../mql`, `PATCH .../ficha`, `PATCH .../modo`, `PATCH .../pecas/{id}` | as escritas, cada campo onde já morava no V1 (a última aprova uma peça de copy) |
| `GET/PATCH /api/motor/chave-geral`, `POST .../recomendacoes/{id}/decisao` | a chave geral e a decisão do gestor (negar, ou aceitar o que não escreve na Meta) |

Configuração: copie `BlueOS.Api/appsettings.Local.exemplo.json` para `appsettings.Local.json`
(ignorado pelo git) com a **anon key** do Supabase (pública) e um dos dois transportes:
a **connection string do pooler** com a senha do banco (Npgsql, o alvo do plano; Project
Settings, Database, **Session mode, porta 5432**, porque a conexão direta é só IPv6 e o
modo transaction da 6543 quebra com o pool do Npgsql: medido em 30/10/2026, uma chamada em
70 ms e a seguinte em timeout de 30 s, enquanto a 5432 fica estável em 60 a 100 ms; o pool
fica em 5 conexões porque em session mode cada uma segura um backend no Postgres), ou,
como ponte, o **Supabase:AccessToken** da Management API (o mesmo
`SUPABASE_ACCESS_TOKEN` que o gate de deploy do V1 usa): aí cada tela é uma chamada HTTPS
a `api.supabase.com`, mais lenta que o Npgsql, e o token é de conta, mais poderoso que uma
senha de banco. É ponte, não destino. Em qualquer transporte a API **recusa o ref de produção**
(`oaeqvfmvjahmlgdhpsrv`); ela só fala com o projeto do `Supabase:Url`, a homologação.
A senha do banco da homologação (`Blue-V1-Interno-STAGING`, ref `yurhgwqilbgtkvoymyab`) foi
definida em 30/10/2026 pela Management API e mora só no `appsettings.Local.json`; quem precisar
dela redefine no painel do Supabase (Project Settings, Database) e grava a sua. Atenção à rede:
o provedor da casa do Igor bloqueia as portas de Postgres (5432 e 6543); pelo hotspot do celular
o pooler responde. Se o `/api/health` disser "Failed to connect" ao pooler, teste a porta com
`Test-NetConnection aws-0-sa-east-1.pooler.supabase.com -Port 6543` antes de mexer na API.
`dotnet run --project BlueOS.Api` sobe em `http://localhost:5201`; `GET /api/health` diz o que
falta. No app, "Fonte de dados" tem a opção **API .NET**, com o endereço do V1 opcional para
as ações que ainda passam por ele. Variáveis `BLUEOS_API_URL` + `BLUEOS_V1_LOGIN/SENHA`
(e `BLUEOS_V1_URL` opcional) abrem o app já ligado à API.

## O executor e o Publicar na Meta (trilha B, 30/10/2026)

A escrita na Meta foi portada do V1 para a API .NET: o **Publicar do Start** (`lib/trafego/publicar/start.ts`,
`peca.ts`, `pedido.ts`, `post-da-pagina.ts`) e o **executor** do aceite de PAUSAR e DUPLICAR
(`lib/trafego/motor/executor.ts`, `aplicar-meta.ts`, `registro.ts`, a rota `motor/decisao`). O desktop na
fonte **API .NET** publica e aceita sem o V1; o que ainda passa pelo V1 é a carga (rodar o motor), o token
do squad, o upload de assets e os agentes de Copy e Design.

| Onde | O quê |
| --- | --- |
| `BlueOS.Core/Meta/GraphRegras.cs` | as regras da Graph sem rede: classe do erro, header BUC, corpo de cada ação, a trava de escrita, o muro do App Mode |
| `BlueOS.Core/Executor/Travas.cs` | as travas de dinheiro (folga do mês D2, 4 mudanças de verba/hora, 20 escritas/rodada, leitura ao vivo) e o texto da cópia |
| `BlueOS.Core/Publicar/StartRegras.cs` | o formulário da régua, os corpos de campanha/conjunto/criativo, o hash da estrutura (o MESMO JSON do V1), nomes, UTMs, a cola do gestor |
| `BlueOS.Api/Infra/MetaGraph.cs` | a Graph por HTTP: GET, POST com `validate_only`, a foto não publicada na página, a imagem do Storage |
| `BlueOS.Api/Dominio/MetaEscrita.cs` | a credencial do squad (`trafego_contas` + `trafego_integracoes`), a saúde do token (`debug_token`), o isolamento por cliente, `AplicarNaMeta` com o anti-ban de 5× |
| `BlueOS.Api/Dominio/Registro.cs` | a reserva idempotente (`trafego_motor_aplicacao`), a decisão append-only, o sino, o log de ação e a trilha |
| `BlueOS.Api/Dominio/Executor.cs` | `AplicarRecomendacao` e a rota do aceite (reconferir a árvore, chave geral, automação do cliente, reserva, aceite relido, escrita, desfecho) |
| `BlueOS.Api/Dominio/Publicador.cs` | o Publicar passo a passo, gravando cada id em `trafego_start_publicacao`, com retomada pelo hash |

Rotas novas: `POST /api/clientes/{slug}/publicar` (corpo `{ pilotos: {MOFU: arteId, BOFU: arteId} }` ou
`designerPecaIds[]`, mais `politicaUrl`, `link`, `formularioId`, `nomes`, `etapas`, `interesses`) e o
`POST .../recomendacoes/{id}/decisao`, que agora APLICA o aceite. Os 85 testes cobrem as regras puras,
inclusive o hash da estrutura byte a byte contra o V1.

**A trava de escrita (AD-MT-20).** Esta API só fala com a homologação, e a conta de anúncio é a MESMA da
produção. Toda escrita exige, no `appsettings.Local.json`, `Meta:EscritaForaDaProd = true` **e** a conta em
`Meta:ContasTeste`; fora disso o caminho é só ensaio (`validate_only`) e a resposta vem com `simulado: true`.
`Meta:AppId`/`Meta:AppSecret` (o app da Meta) ligam o `debug_token` da saúde do token; sem eles a saúde sai
`nao_conferida` e não barra. O `GET /api/health` mostra a trava em `meta`.

**O que diverge do V1, de propósito:** o vault não é lido (os limites são os duros do código e a cópia nasce
PAUSED, o lado conservador); o crivo da publicação é o crivo em código (`Core/Copy/GeradorDemo.cs`); a
reconferência da árvore compara ação e chaves, não o sha do vault; a imagem da arte é lida pela
`storage_url` (a API não tem a chave do bucket).

**Fumaça em 30/10/2026**, pela Management API, usuário de QA, cliente `savana-app` (PIRATAS): o Publicar
ensaiou campanha e criativo na Meta (os dois passaram: o app do squad está em Live), com 4 conjuntos a criar,
e parou como `simulado`; o aceite de um DUPLICAR passou pela reconferência, chave geral, reserva, decisão
gravada, isolamento confirmado na Meta, leitura ao vivo (ACTIVE, R$ 33/dia, conta 1) e travas, e parou na
escrita (`simulado`), fechando a aplicação como `falhou` para a reserva reabrir quando a conta de teste
existir.

## A escrita pelo MCP oficial da Meta (31/10/2026)

O token do app de desenvolvedor não cria criativo nem post (o muro do App Mode). O token dos **conectores de
IA da Meta** (o que o MCP oficial usa, com o escopo `ads_mcp_management`) escreve pelo MCP em
`https://mcp.facebook.com/ads`, por JSON-RPC com `Authorization: Bearer`, sem sessão. O mesmo token serve na
Graph para o que o MCP não oferece. A API usa os dois, com o token da linha do squad em
`trafego_integracoes` (nada de token na configuração):

| Passo | Por onde | Ferramenta |
| --- | --- | --- |
| a conta está no token e habilitada no MCP? a página aceitou os termos de lead? | MCP | `ads_get_ad_accounts`, `ads_get_ad_account_pages` |
| o ensaio (validate_only) da campanha e do criativo | Graph | `/act_x/campaigns`, `/act_x/adcreatives` |
| o formulário da régua | Graph (token da página) | `/{page}/leadgen_forms` |
| a arte na biblioteca da conta | MCP (URL do Storage), com os bytes pela Graph se o MCP recusar | `ads_creative_upload_media` |
| campanha, conjuntos, anúncios (o criativo INLINE, com o formulário no botão e as UTMs) | MCP | `ads_create_campaign`, `ads_create_ad_set`, `ads_create_ad` |
| tirar do rascunho, PAUSADO | MCP | `ads_activate_entity` com `publish_as_active = false` |
| PAUSAR, a verba da cópia, ativar a cópia | MCP | `ads_update_entity`, `ads_activate_entity` |
| DUPLICAR (o MCP não copia conjunto) | Graph | `/{adset}/copies` |

O MCP cria em **rascunho** ("draft mode"): os ids voltam com `status = DRAFT` e só existem na conta depois
da publicação do rascunho; a retomada descobre o que ainda é rascunho perguntando à Graph se o id existe ao
vivo. As regras sem rede estão em `BlueOS.Core/Meta/McpRegras.cs` (argumentos de cada ferramenta, leitura do
envelope, classe do erro, a trava); o HTTP em `BlueOS.Api/Infra/MetaMcp.cs`; a rota em `Credenciais.Rota`
(`MetaEscrita`), e cada resposta/trilha diz `via: "mcp"` ou `"graph"`.

**Quem escreve pelo MCP:** `Meta:Mcp:Squads` (lista de squads). Com `Meta:EscritaForaDaProd = true`, o
cliente de um squad da lista escreve de verdade; os outros continuam no ensaio (ou pela conta em
`Meta:ContasTeste`). Começa pelo **POSEIDON**: o token do POSEIDON na homologação (29/10) é o do MCP, e
alcança 91 contas, 77 habilitadas no MCP (lançamento gradual: `is_ads_mcp_enabled`). Dos clientes POSEIDON com
conta cadastrada, só `batcar`, `bonifq-sm`, `ieprev`, `leanspay` e `az-collections` estão no token e
habilitados; `leanspay` ainda não aceitou os termos de lead da página (o Publicar recusa com
`sem_tos_leadgen` e o link). O `GET /api/health` mostra `meta_mcp`.

**Fumaça em 31/10/2026** (só ensaio, `EscritaForaDaProd` desligado, usuário de QA): `leanspay` parou em
`sem_tos_leadgen`; `bonifq-sm` e `ieprev` passaram pela conta e pela página no MCP e pelo ensaio da campanha
na Graph, e o ensaio do criativo bateu no muro do App Mode do app do squad ("BlueOcean - Trafego", em
desenvolvimento), que é o motivo de o token de desenvolvedor "não fazer postagens"; pelo MCP o criativo é
criado pelo app da própria Meta, então a publicação segue. Nada foi criado.

## A geração dentro da API: Copy e Designer (31/10/2026)

O piloto automático do Start parava em "precisa de você: esta ação ainda passa pelo V1" quando faltava copy
ou arte. Os dois agentes foram portados para a API, sem proxy para o V1:

| Onde | O quê |
| --- | --- |
| `BlueOS.Core/Copy/PromptDoCopy.cs` | o Agente de Copy sem rede: o vault lido das notas (regras fechadas, orientação geral e de nicho), o contexto do cliente em markdown, o system na ordem fixa, os pedidos (gerar, regerar, ajustar), o esquema JSON com a estrutura declarada, o crivo (declaração e marcadores de texto), a divisão geral/nicho |
| `BlueOS.Api/Regras/regua-copy-v8.md` | a régua V8, o mesmo arquivo do V1 (`lib/regras/copy/Regras do Agente Copywriter.md`) |
| `BlueOS.Api/Infra/Ia.cs` | o modelo de texto (SDK da Anthropic): saída estruturada no esquema, cache do primeiro bloco, cadeia criativo (Opus 5.5 → Opus 5) e ajuste (Sonnet 5.5 → Sonnet 5), só sobrecarga passa ao modelo seguinte |
| `BlueOS.Api/Dominio/CopyAgente.cs` | gerar o lote (`copy_lote`, `copy_lote_item`, `copy_peca` com etiqueta, origem, sha do vault e crivo; regera UMA vez a reprovada) e ajustar uma peça (versão anterior em `copy_peca_versao`); cada chamada em `ia_uso` |
| `BlueOS.Core/Design/PromptDaArte.cs` | o Designer sem rede: o rodízio de cenas por funil, número e lote, o contexto visual e os elementos de apoio, o TEMA da peça (instrução, esquema, travas de objeto proibido e de copy citada), o prompt da casa SEM TEXTO, o caminho no Storage, as camadas de `texto_layout` |
| `BlueOS.Core/Design/Diagramacao.cs` | as caixas de headline, sub e CTA em frações da peça (feed 1080x1350, story 1080x1920), com a faixa calma do topo onde a logo real entra depois |
| `BlueOS.Api/Infra/ImagemIa.cs` | o modelo de imagem: OpenAI (`gpt-image-2.5-sunburst`, o padrão do V1; `OPENAI_IMAGE_MODEL` troca) ou Gemini, pela chave que existir |
| `BlueOS.Api/Infra/Compositor.cs` | a cena enquadrada, o plano de contraste e o texto em Poppins (as fontes do V1, em `Fontes/`), com a quebra de linhas pela medida real da fonte; a paleta dominante da logo quando o cliente não tem cores cadastradas |
| `BlueOS.Api/Infra/SupabaseStorage.cs` | subir, apagar, baixar e assinar no Storage; a `service_role` vem da Management API em tempo de execução (nunca em arquivo) |
| `BlueOS.Api/Dominio/Designer.cs` | uma chamada por peça e formato: tema, prompt, cena, camada de texto, Storage (`designer-files`, caminho do V1), `designer_lotes`/`designer_pecas` (upsert por lote, funil, número e formato), `ia_uso`; `encerrar` fecha o lote |
| `BlueOS.Api/Infra/Segredos.cs` | as chaves dos provedores: `Segredos:NOME`, variável de ambiente, ou os `.env` listados em `Segredos:Arquivos` (o `.env.local` do V1 serve sem copiar a chave) |

Rotas: `POST /api/clientes/{slug}/copy/gerar` (`{ itens: [{tipo, funil, nPecas}], briefing, referencia, nome }`),
`POST /api/clientes/{slug}/pecas/{id}/ajustar` (`{ instrucao, campos }`), `POST /api/clientes/{slug}/artes/gerar`
(`{ pecaIds, formatos, template, designerLoteId, encerrar, ajuste }`). O desktop na fonte API .NET usa as três;
o V1 saiu do caminho do piloto automático (ficha, régua, copy, aprovação, arte, piloto, publicação, modo).

**O que diverge do V1, de propósito:** o modelo de copy é o Opus 5.5 (o V1 usa o Opus 5); o Designer porta o
modo da casa (centralizado, texto em camada, logo depois) e não o modo "livre" (o loop agêntico com skill), nem
a extensão do feed para story (a story é gerada de novo em 9:16), nem a leitura das caixas por visão, nem as
telas e a logo como referência do modelo; a paleta vem de `cliente_brand` (ou da logo).

**O resto que ia ao V1 também entrou na API** (`Dominio/Setup.cs` e `Dominio/MotorRodada.cs`): assets da marca
(`POST/DELETE /api/clientes/{slug}/assets`, bucket privado `brand-assets`, prévia por URL assinada), a conta de
anúncio (`PATCH /api/clientes/{slug}/conta`), o token do squad (`GET/POST /api/squads/{nome}/token`, conferido em
`/me` e `/me/permissions` antes de gravar), criar cliente (`POST /api/clientes`, coordenador pra cima, com o
vínculo principal de squad) e rodar o motor (`POST /api/clientes/{slug}/motor/rodar`: a árvore do Core sobre a
carga do banco, grava `trafego_motor_recomendacao` sem repetir a pendente; a carga da Meta continua sendo o cron
do V1, e a leitura do texto pelo modelo via OpenRouter ficou de fora: `texto_fonte = trilha`). O cliente da API
no desktop não tem mais nenhuma chamada ao V1.

**Fumaça em 31/10/2026 (HD Tecnologia, homologação):** a copy de BOFU saiu em 20 s pelo Opus 5.5 (vault
ebe4c4f, nicho `industria-logistica`, crivo ok), e a arte em 47 s (tema pelo Sonnet 5.5, cena `icp`, imagem
pelo gpt-image-2.5-sunburst, texto em Poppins), gravada em `designer-files` e visível na tela do cliente.

## Homologação em dia com a produção (`scripts/sync_prod_para_stag.py`)

Os clientes que o token do MCP alcança existem na produção, e a homologação tinha ficado para trás
(vínculos de squad, contas de tráfego, régua). O script lê a produção (SÓ `select`, com o token da
Management API em `appsettings.Local.json`) e escreve na homologação: `clientes`, `trafego_contas`,
`cliente_squads`, `cliente_estrategia` e `trafego_integracoes`. A produção vence nas linhas que diferem; os
ids de cliente e de squad da homologação são preservados (mapeados por slug e por nome); nada é apagado (o
squad principal vigente que só existe na homologação é ENCERRADO com `ate = hoje`); o token de um squad só é
trazido quando o da produção é mais novo. Idempotente: rodar de novo aplica só a diferença. Rodado em
31/10/2026: 13 clientes, 32 contas de tráfego, 514 vínculos de squad, 1 estratégia e 2 integrações
(PIRATAS mais novo, VIKINGS que faltava); antes, um backup das cinco tabelas foi gravado no scratchpad.

## O Agente de CS na API: a base das telas dos coordenadores (30/10/2026)

As telas por cargo nascem da tela do gestor, e a do gestor nasce do Agente de CS. Por isso o CS entrou
primeiro, e só como endpoints (porte de `app/api/os/me` e `app/api/cs/*` do V1). As regras são puras e
testadas em `BlueOS.Core/Cs/CsRegras.cs` (nota 40% valor / 60% retenção; faixas 0-49 crítico, 50-65
atenção, 66-79 saudável, 80+ excelente; `sem_base` quando nenhum pilar tem fonte; cargos por tier; alcance
por qualquer squad; dinheiro visto de um squad; grupo frio de 3 dias corridos em Brasília; situação do
prazo; validação e merge das decisões humanas). O SQL mora em `BlueOS.Api/Dominio/Cs.cs`.

| Rota | O que devolve |
|---|---|
| `GET /api/me` | perfil, papéis, `escopo_squads` (nomes), tier e os `pode_*` (sem performance, criar cliente, setup CS, administrar, aplicar na Meta) |
| `GET /api/cs/carteira[?squad=id\|nome&grupo=frio]` | os cinco status da aba Clientes com a última análise (do principal, ou do squad do recorte), MRR/TCV/incluso pelo ponto de vista do squad, nota, faixa, tendência, upsell e grupo frio medido. UMA consulta (o V1 fazia dezenas, em lotes) |
| `GET /api/cs/carteira?cliente=id[&squad=]` | o dossiê: análise completa do squad escolhido (pedido, senão principal), histórico de 30 notas, abas de squad com `tem_analise` e o botão Sem Performance (ABYSSAL/DIVERS fixos) |
| `GET /api/cs/risco[?todos=1]` | o alerta de churn: últimas análises alto/crítico com nota, os 8 piores (ou todos), `total_em_risco` e `corte_declarado` |
| `GET/POST/PATCH /api/cs/anotacao` | anotações do time (listar, gravar, ARQUIVAR; nunca apaga) |
| `GET/POST/PATCH /api/cs/upsell-vendido` | vendas registradas + catálogo; slug do catálogo ou nome livre; arquivar |
| `PATCH /api/cs/passo-decisao` | `fechado \| ignorado` com justificativa obrigatória, gravado AO LADO do passo (`proximos_passos_decisao`, merge por índice) |
| `PATCH /api/cs/promessa-status` | o status humano de uma promessa do alinhamento comercial (`alinhamento_status_decisao`) |
| `POST /api/cs/sem-performance` | liga/desliga a Performance do cliente num squad: coordenador e acima, vínculo vigente, 409 nos squads fixos |
| `GET /api/cs/membros?cliente_id=` | quem pode receber a task do cliente (membros dos squads vigentes dele) |
| `POST /api/cs/passo` | o próximo passo vira task (`tasks`, origem `cs`): título curto pela IA (corte na palavra se falhar), responsável pelo primeiro nome casado nos membros do cliente, dedup pelo texto aberto |

Escopo: leituras pelo vínculo efetivo (vigente; sem vigente, o encerrado), fora do squad interno; escritas
pelo vínculo em qualquer squad e **404** (nunca 403) fora do alcance. Fumaça na homologação pelo pooler
(`qa_trafego`, escopo global, 518 clientes): `/me` 105 ms, carteira 548 ms, recorte por squad 184 ms,
dossiê 184 ms, risco 153 ms; as escritas todas passaram e os rastros foram arquivados.
O que ficou de fora de propósito: o motor da análise (LLM, ~3.800 linhas no V1), os crons, o workspace
(Composio/Meet) e o WhatsApp inbound — a API lê o que o V1 grava.

## O Panorama: o mapa estelar do squad (07/11/2026)

A tela inicial do gestor passou a ser o **Panorama** (`Pages/Panorama.razor`, rota `/`); o Start (carteira de
cards e stepper) continua inteiro em `/start`. O desenho veio do trabalho de UX em
`_bmad-output/initiative-fluxo-gestor-trafego/ux-panorama-carteira/` (DESIGN.md, EXPERIENCE.md e o mock
`mockups/panorama-orbita.html`, escolhido pelo Igor "exatamente dessa maneira"). O tema novo ("Órbita":
navy, vidro, Outfit, azul #003CFF só como bloco, aqua #00DEB9 como o verde do gestor) vale para o app todo;
os tokens antigos de `app.css` continuam existindo, mapeados para a paleta nova, então o Start herdou o visual
sem mudar de estrutura.

**O mapa.** Cada cliente do squad é uma estrela do mesmo tamanho; só a cor fala. A gravidade puxa para o centro:
anel 1 (1 automação parou · 2 dinheiro), anel 2 (3 risco de churn · 4 travado numa etapa), anel 3 (5 pedido do
cliente · 6 cliente frio); os em dia orbitam na borda, em ordem alfabética a partir do topo. No centro, o placar
vivo de saúde do squad (em dia ÷ total). Clicar numa estrela abre o aprofundamento preso a ela: por quê, o que
aconteceu (linha do tempo), o que custa, o que resolve e quem pode; as ações são **Abrir no Start** (deep link na
etapa certa) e **Marcar como visto** (silencia até amanhã ou até a assinatura do estado mudar). A busca do topo
(`/`) acende a estrela achada e escurece o resto; Enter abre, Esc limpa. Só a gravidade 1 interrompe: sino com
badge e faixa vermelha em qualquer tela. Resolver um problema acende a estrela (vermelho → verde, com onda),
sobe o placar e dá os pontos da gravidade a quem olha (+60/+40/+30/+20/+10/+10).

**As regras são puras e testadas** (`BlueOS.Core/Panorama`, `BlueOS.Tests/PanoramaTests.cs`):
`Gravidade.cs` (a ordem de atenção fechada com o Igor: dinheiro acima de churn, pedido abaixo de travado),
`Diagnostico.cs` (de um `Cliente` aos alertas; "em automação" é ter a campanha publicada, porque o token vencido
rebaixa o estágio para Assets mas a conta segue rodando), `Orbita.cs` (anéis, ângulos, segunda volta quando não
cabe) e `Panorama.cs` (o `Montador`: estrelas, resumo do squad, vistos). O que o mapa lê além do fluxo vive em
`Cliente.Sinais` (`Modelos/Sinais.cs`): nota, faixa e risco do CS, dias sem mensagem, pedido do cliente,
automação pausada, KPIs do mês.

**Fontes.** `IBlueApi` ganhou `LerPanoramaAsync(squad)` e `MarcarVistoAsync`. A demonstração (`DemoApi.Panorama.cs`,
`DemoFabrica.cs`) tem 50 clientes do POSEIDON: os 5 do fluxo mais Ferraz Contábil (token vencido), Batcar (budget
acabando), Leanspay (pedido pelo CS), AZ Collections (grupo frio), Escola Aurora com churn crítico e 41 em dia;
placar, ranking e "acendeu hoje" semeados; pontos de verdade quando um alerta some entre duas leituras. No V1 e na
API .NET o Panorama é montado **no app** sobre a carteira (`PanoramaLocal.cs`): vistos na memória da sessão,
placar zerado, sem CS nem pedido do cliente até a carteira da API trazer `Sinais`.

**O que falta para o Panorama real** (EXPERIENCE.md, "Data contract"): a rota `GET /api/panorama` na API com os
KPIs em lote (hoje é um `ongoing` por cliente), os sinais do CS e o token do squad numa consulta; a tabela do visto
(`trafego_panorama_visto`) e a dos pontos (`trafego_panorama_acendeu`); a conferência periódica do token e a
"última leitura" por fonte para os sinais de vida; e o push (SignalR) para a tela ser em tempo real de fato. Hoje o
app relê a cada 60 s e a cada mudança da fonte.

## O Start no tema Órbita: carteira de ação, trilha e "Falta" (07/11/2026)

O Start (`/start`) passou a falar a língua do Panorama (UX em `_bmad-output/initiative-fluxo-gestor-trafego/ux-start-cliente/`,
mocks `start-foco.html` e `start-trilha.html`; a escolha do Igor foi uma mistura: carteira, Assets e painel do Start do
"Foco", Automação do "Trilha"). **A carteira mostra só quem precisa de ação** (alerta do Panorama, decisão pendente ou
condutor parado), por gravidade; os em dia aparecem pela busca. O card tem a etiqueta e o halo da gravidade, a frase
curta do alerta, "o que resolve" e o botão com **a ação que resolve** ("Trocar o token", "Ver as 4 decisões", "Conectar a
conta", "▶ Start"); chips todos / começar / terminar / arrumar (`Core/Panorama/AcaoDoCard.cs`). Dentro do cliente: a
**faixa de comando** com o alerta e a ação primária, a **trilha vertical** (`Componentes/Trilha.razor`, no lugar do
stepper: etapas feitas dobradas numa linha, a atual em checklist, as futuras com uma pista) e a etapa à direita. Nas
etapas do meio o bloco **"Falta para sair de …"** vem antes do resto (em Assets, com os campos e botões da conta, da
página e do token); a Automação segue a ordem de hoje com os cards de decisão enxutos (frase, números-chave,
Aceitar/Negar, trilha só sob demanda). O jargão de desenvolvedor (migration, vault, ids da Meta, ACTIVE/PAUSED) saiu
das telas. O painel do Start separa "o condutor já achou" de "falta de você".

**Pontos a caminho.** Agir no Start (aceitar/negar decisão, conferir token, conectar conta, dar Start) registra
`RegistrarACaminhoAsync`; o Panorama mostra "+N pts a caminho · Cliente" em cinza no placar e vira pontos de verdade
quando a estrela acende. Na demo e no app é memória; na API falta a tabela (EXPERIENCE.md do Start, "Data contract").

## Próximos passos sugeridos

1. A rota `GET /api/panorama` na API (KPIs em lote, CS, token, vistos e pontos persistidos) e o push por SignalR, para o Panorama sair da demonstração.
2. A primeira publicação de verdade pelo MCP num cliente POSEIDON com régua, artes e página com os termos de lead aceitos (`az-collections` tem a página; falta a régua e as artes).
3. O worker da carga diária (rodar o motor) na API; o desktop só troca o endereço.
4. As telas dos coordenadores sobre os endpoints do CS; depois os outros cargos (Designer, SEO) na mesma biblioteca.
5. Distribuição do desktop (MSIX ou Velopack) e o repositório próprio com CI.
