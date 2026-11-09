# Plano de validação

Os testes unitários em `tests/NetStability.Core.Tests` validam cálculo de média/mínimo/máximo, perda, jitter sem atravessar sondas perdidas, score sem dados e o escopo da regra de TCP autotuning. O aplicativo também executa e guarda uma comparação de janelas antes/depois. Nenhum resultado de rede real é embutido como se tivesse sido medido.

## Verificação em Windows

1. O build cruzado local em Debian 13 ARM64 com SDK .NET 10.0.403 concluiu sem erros/avisos e produziu o executável Windows x64 autocontido. Em 9 de novembro de 2026, o workflow Windows `Windows build` passou: `dotnet test` e publicação do executável foram concluídos com sucesso. O runner VSTest do proot ARM64 local não conseguiu localizar o host .NET. O executável ainda precisa de validação interativa e medições em hardware Windows/rede real; este ambiente não consegue fazer essa validação.
2. Em uma conexão cabeada estável, execute três diagnósticos sem carga. Confirme que o gateway fica baixo/estável e que média, min/máx e jitter refletem as oito amostras; a perda deve corresponder às sondas sem resposta.
3. Em Wi-Fi, repita junto ao roteador e no local normal de jogo. Compare gateway, sinal/canal e BSSIDs vistos no canal. Depois repita via Ethernet. O sinal/canal ajudam a investigar, mas não medem RF diretamente.
4. Configure um endereço de jogo que aceite ICMP. Deixe o monitor ativo durante uma partida e confirme a linha e métricas `Jogo`. Se o host bloquear ICMP, o app deve mostrar amostras ausentes e explicar que o resultado é inconclusivo.
5. Em uma janela sem downloads, registre latência e taxa passiva; depois repita durante uma transferência voluntária. Um pico simultâneo a tráfego maior deve gerar apenas uma suspeita de fila, nunca um diagnóstico conclusivo de bufferbloat. Para confirmar, use um teste controlado de latência sob carga e ajuste SQM/AQM no roteador se necessário.
6. Compare gateway, destino externo e `tracert`. Variação no gateway com externo igual ou pior justifica investigar PC, rádio/cabo e roteador. Gateway estável com externo pior justifica investigar VPN/provedor/rota/destino. Saltos `*` não provam perda encaminhada.
7. Faça o teste de caminho MTU e valide se ICMP grande é respondido. Resultados parciais devem ser tratados como inconclusivos; não há alteração automática da MTU.
8. Em uma VM de teste, altere manualmente o TCP autotuning para `restricted`, registre um diagnóstico, clique em **Otimizar com justificativa**, aceite a elevação e confirme a leitura `Normal`. Confira que o relatório tem as duas janelas. Use **Restaurar** e confira o retorno ao valor original. Repita cancelando o UAC e verifique que nenhum ajuste foi feito.
9. Verifique em Windows 11 x64 e na edição/build Windows 10 a ser suportada. A edição consumer de Windows 10 encerrou suporte geral em 2025; leia as notas de compatibilidade antes de distribuir.

O teste 8 deve ser feito em VM ou dispositivo de laboratório: muda um parâmetro TCP global do sistema. A suíte automatizada não realiza mudanças administrativas nem satura uma conexão.
