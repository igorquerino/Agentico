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

1. **Chegada**: o cliente entrou.
2. **Assets**: logo principal, logo variação, telas do produto (bucket `brand-assets`), conta
   de anúncios (`act_`), página, token do squad conferido na Meta, lista do cliente, site/pixel,
   política de privacidade e link.
3. **Ficha**: cadastro, ICP, produto, estratégia, o card Tráfego (nicho e budget mensal) e o
   critério de MQL (régua v2 e teto por MQL do PPL). São os mesmos campos do card do cliente
   do V1.
4. **Copy**: o agente escreve as peças das etapas que a **faixa do budget** pede (f1: só MOFU;
   f2: MOFU e BOFU; f3: TOFU, MOFU e BOFU), com a estrutura declarada (ângulo, papel da sub,
   tipo de CTA) e o crivo em código (régua V8 e regras fechadas do vault). O gestor aprova.
5. **Design**: as artes das peças aprovadas (feed e story) e a escolha do criativo piloto.
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

## Próximos passos sugeridos

1. Ligar a fonte V1 contra o localhost:3080 e corrigir os corpos que divergirem.
2. A API .NET (trilha B do plano) e o worker da carga diária; o desktop só troca o endereço.
3. As telas dos outros cargos (CS, Designer, SEO) na mesma biblioteca.
4. Distribuição do desktop (MSIX ou Velopack) e o repositório próprio com CI.
