# CashPilot

Controle de fluxo de caixa pessoal para quem tem várias contas, cartões, boletos e recebíveis. A pergunta central: **qual é a forma mais barata de cobrir meus pagamentos neste mês?**

## Objetivos

- Importar extratos (CSV/OFX primeiro; depois PDF e imagem) sem duplicar lançamentos.
- Classificador que **aprende** com as suas correções.
- Pix entre contas próprias e pagamento de fatura **não** contam como gasto.
- Juros agrupados em "Juros e encargos" por tipo: cheque especial, atraso de boleto, atraso de cartão, saque na maquininha e tarifas.
- Calendário de pagamentos, limites de contas e cartões e simulador de juros.

## Rodar

```
dotnet test
```

## Importar a planilha

Exporte a aba "Gastos" como CSV para a pasta `data/` (ignorada pelo git):

```
dotnet run --project src/CashPilot.Cli -- import-gastos data/gastos.csv
dotnet run --project src/CashPilot.Cli -- apply-rules data/rules.csv   # pattern,category,item
dotnet run --project src/CashPilot.Cli -- pending
dotnet run --project src/CashPilot.Cli -- classify "Nome da Loja" "Compras" "Geral"
```

O banco fica em `data/cashpilot.db` (mude com `--db`). Importar o mesmo arquivo de novo não duplica nada.

## Saque na maquininha

O cartão é cobrado pelo valor bruto, a conta recebe o líquido, e a diferença é a despesa.
R$ 1.000 a 3,09% (1x): custo R$ 30,90, líquido R$ 969,10. A tabela de taxas por parcela (`CashAdvanceFeeTable`) já vem com as taxas de crédito da maquininha, de 1x a 12x (3,09% a 12,38%).

## Próximos passos

- Importação pela web (envio do CSV) e cadastro de categorias e regras
- Cadastro de contas (limite, fechamento, vencimento)
- Tela "Contas a pagar" (feita): faturas dos cartões (pelo fechamento e vencimento), boletos avulsos e lançamentos futuros, ao lado das contas bancárias e saldos (saldo informado por você + lançamentos depois dele)
- Cheque especial (LIS) por conta: dias sem juros e taxa de juros
- Calendário de vencimentos e simulador de custo (feitos: LIS, saque na maquininha e atrasar um boleto, com a multa e os juros de mora cadastrados em cada boleto; mais o rotativo do cartão, com a taxa de cada cartão cadastrada em Contas e o IOF, e atrasar a fatura com multa de 2% e mora de 1% ao mês)
- Importação de extratos (OFX de qualquer banco, CSV e PDF do Bradesco, PDF do Itaú) com prévia antes de gravar, sem duplicar e reconhecendo transferências entre as suas contas; faltam PDFs de outros bancos
- Imagens de fatura/extrato (prints do app) lidas por OCR local e gratuito (Tesseract; precisa de `data/tessdata/por.traineddata`, fora do git); o texto lido aparece para você revisar antes de importar
- Pagamento de fatura casado com a fatura (mesmo valor, feito entre o fechamento e 10 dias depois do vencimento): fatura paga sai de "O que falta pagar" e do calendário, e a tela mostra as faturas recentes e os pagamentos sem fatura; quando o valor não bate, dá para marcar a fatura como paga à mão (isso só a tira da lista, sem lançar o pagamento)
- Parcelas futuras: a partir da última parcela já importada de cada compra no cartão, mostra o que ainda vai cair em cada fatura (por mês de vencimento) e quanto já está comprometido
- Comparativo mensal: gasto por categoria no mês contra o mês anterior e a média recente, destacando as categorias que subiram bastante
- Orçamento por categoria: limite mensal por categoria, quanto já foi gasto, quanto resta, aviso ao chegar em 80% e ao estourar, projeção no ritmo atual e sugestão de limite pela média dos meses anteriores
- Previsão de caixa no Calendário: o que se repete todo mês (aluguel, assinaturas, salário), as parcelas que ainda vão cair e as faturas dos próximos meses entram no saldo projetado, com o primeiro dia no vermelho, o menor saldo e se passa do LIS (é uma estimativa, e pode ser desligada)
- Reembolsos do plano de saúde: o gasto continua lançado pelo valor pago; você registra o pedido, e quando o Pix do plano chega liga-o a um ou vários pedidos (o app sugere a combinação que soma o valor do Pix). O Pix abate a categoria no mês em que entra, a diferença que o plano não devolveu aparece como custo real, a tela guarda o histórico por mês do gasto (pago, reembolsado, aguardando, não devolvido, custo líquido) e a lista dos pedidos já recebidos com cada Pix, e os pedidos pendentes entram na previsão de caixa
- Lançamento manual (dinheiro, Pix que ainda não chegou no extrato): o app classifica pelo que já aprendeu ou deixa em Pendentes
- Pendentes com sugestão de categoria (pela descrição parecida já conhecida) e botões para preencher e salvar várias linhas de uma vez, sempre com a sua confirmação
- Backup: tela para baixar a cópia do banco e os lançamentos em CSV, e cópia automática diária em `data\backups` (guarda as 14 mais recentes)
- Exclusão manual de lançamentos (a exclusão é lógica: reimportar o mesmo arquivo não traz de volta) e aviso rápido ao salvar
