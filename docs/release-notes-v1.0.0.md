# NetStability v1.0.0

Primeira versão pública do diagnóstico de estabilidade de rede para Windows x64.

## Download e execução

- Baixe `NetStability.exe` nos assets desta release e execute-o no Windows x64.
- O executável é autocontido e não requer instalar o runtime .NET separadamente.
- É um aplicativo portátil; não inclui instalador MSI.
- O binário não tem assinatura digital de código.

## Incluído

- Monitoramento de ping, jitter, perda ICMP e taxas passivas da interface.
- Comparação do gateway, destino externo e alvo de jogo configurável.
- Diagnósticos de DNS, Wi-Fi, MTU de caminho, adaptadores VPN e rota.
- Histórico local, relatório JSON e comparação antes/depois.
- Uma otimização reversível e restrita do TCP receive-window autotuning; nenhuma mudança genérica de DNS, MTU ou propriedades do driver.

## Compatibilidade e limites

- Windows 11 x64 é o alvo principal.
- O suporte oficial do .NET 10 em Windows 10 limita-se a edições LTSC/Enterprise. Windows 10 Home/Pro terminou o suporte geral em outubro de 2025.
- ICMP pode ser filtrado ou limitado. Os resultados são indícios diagnósticos, não garantia de perda real do tráfego do jogo.
- A release passou os testes unitários, build e smoke test de inicialização no runner Windows. Faça medições no seu próprio PC e rede; a aplicação não promete reduzir latência causada pelo provedor, VPN, rota ou servidor do jogo.
