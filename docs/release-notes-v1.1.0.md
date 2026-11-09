# NetStability v1.1.0

## Monitoramento e otimização Wi-Fi automática

- Novo botão **Monitorar + otimizar Wi-Fi**, separado do monitoramento passivo.
- Agente local e privado, com política determinística testada: analisa gateway, sinal, tráfego, alimentação e reversão pendente; explica cada decisão e não envia métricas à nuvem.
- Observa em tempo real o enlace até o gateway e exige degradação persistente antes de agir.
- Se o enlace for Wi-Fi, sinal reportado for de pelo menos 60%, não houver pico de tráfego coincidente, o PC estiver na tomada e o Windows expuser o ajuste, pede UAC e seleciona **Máximo desempenho** no perfil Wi-Fi AC ativo.
- O ajuste não muda o perfil de bateria nem opções do driver/roteador. Salva o índice AC e oferece **Desfazer ajustes** com verificação de que o plano não foi trocado.
- Se houver sinal fraco, congestionamento aparente, conexão cabeada ou configuração indisponível, explica o motivo e não aplica o ajuste.
- O monitor atualiza os dados do adaptador periodicamente e não repete a otimização várias vezes na mesma sessão.

## Interface

- Ações principais simplificadas: **Diagnóstico**, **Monitorar**, **Monitorar + otimizar Wi-Fi** e **Desfazer ajustes**.
- O painel destaca o estado da automação e o modo de energia Wi-Fi detectado.
- Estados de foco, hover, clique e desativação dos botões usam o tema escuro e não recorrem ao template branco padrão do Windows.
- O ajuste TCP ficou em Avançado, com descrição de que se destina a transferências e não promete baixar ping de jogos.

O executável é autocontido para Windows x64 e portátil; não inclui instalador MSI. Teste a estabilidade no seu próprio Wi-Fi: distância, obstáculos, interferência, roteador e roteamento externo não podem ser corrigidos pelo cliente Windows sozinho.
