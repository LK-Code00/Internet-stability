# NetStability para Windows

Aplicativo WPF para Windows 10/11 que mede a consistência da rede e ajuda a localizar se a variação aparece entre o computador e o gateway, ou depois do gateway. O foco é ping, variação entre respostas, perda ICMP e correlação entre latência e tráfego, não a velocidade contratada.

## O que funciona

- Diagnóstico com oito amostras de ICMP ao gateway, ao destino externo configurado e, opcionalmente, ao host do jogo.
- Ping atual, mínimo, máximo, média, jitter (média das diferenças absolutas entre RTTs sucessivos), perda das sondas, score heurístico e gráficos.
- Taxas passivas estimadas de tráfego IPv4 a partir dos contadores da interface. Não é um speed test e não inclui necessariamente tráfego IPv6.
- Resolução DNS pelo resolvedor do Windows, leitura das propriedades avançadas do driver, DNS da interface física, MTU, driver, sinal/canal Wi-Fi e propriedades de gerenciamento de energia expostas pelo adaptador.
- Três sondas IPv4 com bit DF para cargas de 1472, 1400 e 1200 bytes; registra `tracert -d` limitado a oito saltos e 180 ms por tentativa.
- Monitoramento contínuo, histórico local de até 100 diagnósticos, exportação JSON com amostras e rota, achados e comparação antes/depois.
- Suspeita de possível fila/congestionamento quando um pico de latência coincide com tráfego acima da linha de base recente. É uma correlação passiva, não um teste de bufferbloat sob carga controlada.
- Otimização administrativa opcional e restrita: se o TCP receive-window autotuning está desativado/restrito/experimental, permite restaurar `Normal`. O app grava o nível original em `%LOCALAPPDATA%\NetStability\optimization-backup.json`; o rollback verifica que a configuração ainda está no valor que o app aplicou antes de desfazer.
- A consulta detalhada de energia do adaptador pede UAC somente quando o usuário clica no botão avançado; é somente leitura.

## O que o aplicativo não muda

DNS, MTU, RSS, interrupt moderation, economia de energia e opções avançadas da placa nunca recebem valores padrão genéricos. São mostrados para diagnóstico/recomendação. O teste de MTU não muda a placa. Para modificar DNS ou SQM, configure conscientemente o roteador ou o Windows depois de confirmar o problema. Nenhuma opção promete reduzir ping causado pela VPN, pelo provedor, pela rota externa ou pelo servidor do jogo.

O diagnóstico usa ICMP. Roteadores, VPNs e destinos podem limitar ou bloquear respostas; `*` no `tracert` não significa necessariamente perda encaminhada. A consulta `example.com` usa o resolvedor do Windows e pode ser atendida pelo cache. O sinal Wi-Fi vem do Windows/driver e não mede diretamente ruído de RF. Os BSSIDs no mesmo canal sugerem disputa por airtime, não provam interferência. A qualidade 0–100 é uma regra explicável para a janela observada, não um padrão de mercado.

Um diagnóstico ativo envia ICMP ao gateway, destino externo e alvo de jogo, resolve `example.com` pelo DNS do sistema, envia três sondas IPv4 com DF e executa `tracert` limitado. O histórico permanece local; a exportação é iniciada pelo usuário. Não há analytics ou envio de relatórios pelo aplicativo.

## Compilar no Windows

Requisitos: Windows com .NET 10 SDK instalado e acesso ao NuGet para os pacotes de teste. O aplicativo não usa bibliotecas de terceiros.

```powershell
cd Internet-stability
dotnet restore .\NetStability.sln
dotnet test .\tests\NetStability.Core.Tests\NetStability.Core.Tests.csproj -c Release
dotnet build .\NetStability.sln -c Release
```

Publicar um executável autossuficiente x64 (não requer instalar .NET Desktop Runtime):

```powershell
dotnet publish .\src\NetStability.App\NetStability.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o .\artifacts\win-x64
```

O executável será `artifacts\win-x64\NetStability.exe`. Para Windows ARM64, use `-r win-arm64`. Uma build self-contained é maior, mas evita depender do runtime instalado na máquina.

O workflow `.github/workflows/windows-build.yml` executa os testes e publica o executável como artefato em cada push/PR no repositório dedicado.

## Instalar e usar

1. Execute `NetStability.exe`. O app abre sem elevação; diagnósticos básicos não precisam de administrador.
2. Em **Avançado**, configure o host externo e, se souber o IP/hostname do servidor, o alvo do jogo. Salve.
3. Execute **Executar diagnóstico** para criar a linha de base, ou **Iniciar monitoramento** para acompanhar durante uma partida.
4. Compare o RTT do gateway com o externo. Oscilações/perda no gateway apontam para PC, rádio, cabo ou roteador. Gateway estável com externo instável aponta para VPN, provedor, rota ou destino, sem identificar isoladamente qual.
5. Use **Otimizar com justificativa** somente quando o autotuning estiver em nível conhecido como restrito. A operação solicita UAC. **Restaurar** desfaz somente essa alteração feita pelo app.
6. Abra **Histórico e relatório** para revisar e exportar JSON.

Sem uma configuração alterável justificada, a otimização não aplica mudanças e executa uma segunda medição para comparar a variação natural entre as janelas. Se o TCP autotuning for alterado por outra ferramenta após a otimização, a restauração é interrompida para não sobrescrever a escolha mais recente; revise a configuração manualmente.

## Notas de compatibilidade

O projeto compila para `net10.0-windows`/WPF. O .NET 10 é LTS e a Microsoft lista suporte até novembro de 2028. A matriz atual do runtime limita suporte oficial no Windows 10 a edições LTSC/Enterprise; o Windows 10 de consumo terminou o suporte geral em outubro de 2025. O artefato self-contained reduz requisitos de runtime, mas não estende o suporte do sistema operacional. Windows 11 é o alvo principal de produção; validar em hardware Windows 10 específico antes da distribuição.

## Estrutura

```text
src/NetStability.Core/          modelos, estatísticas, score e achados
src/NetStability.App/           WPF, sondas, PowerShell de leitura, UI e rollback UAC
tests/NetStability.Core.Tests/   testes unitários das métricas e regras de otimização
docs/research.md                fontes técnicas e decisões de engenharia
docs/validation.md              plano de validação em hardware Windows
```

## Limitação de entrega nesta cópia

O projeto foi compilado em Debian 13 ARM64 com o SDK .NET 10.0.403. `dotnet build NetStability.sln -c Release --no-restore` terminou com zero erros e zero avisos; a publicação cruzada gerou `artifacts/win-x64/NetStability.exe` (cerca de 62 MiB). O binário é Windows x64 autocontido, mas este ambiente Termux/Android não executa aplicativos Windows.

Os projetos de teste também compilaram. O runner local do VSTest não iniciou no proot ARM64 e retornou “Could not find 'dotnet' host for the 'ARM64' architecture”; por isso a execução dos testes fica pendente da validação Windows. O workflow de GitHub Actions roda os testes em `windows-latest` e disponibiliza o `.exe` como artefato `NetStability-win-x64` em cada push. Abra **Actions → Windows build → execução mais recente → Artifacts** para baixá-lo.
