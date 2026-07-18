# Relatorio para Claude Code - Completar Oferta Seller e Maturidade de Producao

Data: 2026-05-04  
Projeto: Fellow Pay  
Objetivo: transformar o sistema atual em uma oferta de seller/marketplace pronta para escala, com maturidade financeira, operacional, fiscal e de produto.

## Resumo executivo

O Fellow Pay ja oferece uma base forte:

> O sistema ja oferece pagamentos Pix, cartao, debito, boleto, wallets Stripe, payment links, ledger, saldo, payouts, refunds, disputes, split por transacao, regras de split, webhooks, relatorios, dashboard, autenticacao segura e multi-tenant.

Mas antes de vender como marketplace financeiro pronto para escala, ainda precisa fechar:

- idempotencia real do split;
- NF/NFS-e opcional por seller;
- invoices fiscais completas;
- recibos robustos para todos os metodos;
- assinaturas recorrentes maduras;
- simulador de split antes da venda;
- split por produto/item;
- portal seller completo;
- operacao monitorada de payout/refund/retry sem intervencao manual.

O problema principal deixou de ser "feature de checkout". O sistema ja consegue cobrar. O que falta agora e maturidade financeira/operacional:

```text
Feature de checkout = consigo cobrar o cliente?
Maturidade financeira/operacional = consigo cobrar, dividir, estornar, conciliar, auditar,
corrigir e provar tudo sem perder dinheiro?
```

Este documento e um backlog detalhado para o Claude Code completar os pontos restantes.

## Principios obrigatorios

Claude deve tratar estes itens como sistema financeiro real. Toda feature que movimenta dinheiro deve ter:

- idempotencia;
- ledger coerente;
- rastreabilidade;
- reconciliacao;
- auditoria;
- alertas;
- testes de falha;
- rollback/compensacao quando provider falhar;
- documentacao operacional.

Nao basta adicionar endpoint. Cada fluxo precisa responder:

- O que acontece se o webhook chegar duas vezes?
- O que acontece se o servidor cair no meio?
- O que acontece se o provider aceitar mas nosso banco falhar?
- O que acontece se nosso banco salvar mas o provider falhar?
- Como reconciliar?
- Como corrigir manualmente?
- Como provar para seller/plataforma/cliente o que aconteceu?

## Parte 1 - Maturidade financeira e operacional

### 1. Idempotencia real do split

#### Problema

O split ainda nao pode ser considerado 10/10 enquanto `SPLIT_DISTRIBUTE` nao for idempotente no ledger. O risco principal e crash/retry:

- se cair antes do ledger, nao pode pular pagamento;
- se cair depois do ledger e antes do status `PAID`, nao pode pagar duas vezes;
- se o marker estiver `RESERVED` ou `PROCESSING`, o sistema precisa saber se o ledger ja foi executado.

#### O que implementar

Adicionar idempotency key por movimento de ledger:

```text
split:{tenantId}:{transactionId}:{recipientSellerId}:recipient
split:{tenantId}:{transactionId}:{primarySellerId}:primary
```

Ou usar diretamente o `SplitTransfer.Id` como chave de movimento:

```text
split-transfer:{splitTransferId}:distribute
split-transfer:{splitTransferId}:reverse:{refundId}
```

Criar uma protecao em `LedgerService.DistributeFromClearingAsync`:

- antes de debitar `SPLIT_CLEARING`, verificar se ja existe `LedgerEntry` com a mesma operation/idempotency key;
- se existir, retornar sem duplicar;
- se nao existir, executar o double-entry.

Se o modelo atual nao tem campo adequado, adicionar:

- `LedgerEntry.IdempotencyKey`;
- indice unico por `TenantId + IdempotencyKey`, quando `IdempotencyKey` nao for nulo.

#### Criterios de aceite

- Retry apos crash antes do ledger executa distribuicao uma vez.
- Retry apos crash depois do ledger nao duplica distribuicao.
- `SplitTransfer` termina `PAID` quando ledger ja existe.
- `SPLIT_CLEARING` nao fica com saldo residual indevido.
- Dois workers concorrentes nao conseguem duplicar split.

#### Testes obrigatorios

- `SplitDistribute_WhenLedgerAlreadyHasIdempotencyKey_ShouldNotDuplicate`
- `SplitProcessor_WhenCrashAfterLedgerBeforePaid_ShouldMarkPaidWithoutRedistributing`
- `SplitProcessor_WhenCrashBeforeLedger_ShouldDistributeOnce`
- `SplitProcessor_ConcurrentWorkers_ShouldDistributeOnlyOnce`
- `SplitProcessor_WhenLedgerFails_ShouldLeaveRecoverableState`

### 2. Payout/refund/retry sem intervencao manual

#### Problema

Para producao, payout e refund nao podem depender de operador olhando log. O sistema precisa ter retry seguro, compensacao e estado claro.

#### O que implementar

Criar modelo padrao para operacoes financeiras externas:

```text
REQUESTED -> PROCESSING -> SUCCEEDED
REQUESTED -> PROCESSING -> FAILED_RETRYABLE
REQUESTED -> PROCESSING -> FAILED_FINAL
SUCCEEDED -> RECONCILED
```

Aplicar em:

- payout;
- refund;
- split distribution;
- split reversal;
- provider cost adjustment;
- chargeback/dispute settlement.

Cada operacao deve ter:

- idempotency key;
- provider id;
- attempt count;
- last error;
- next retry at;
- final failure reason;
- reconciliation status.

#### Criterios de aceite

- Provider timeout nao gera duplicidade.
- Provider sucesso + DB falha e reconciliavel.
- DB sucesso + provider falha e compensavel.
- Operacoes travadas aparecem em dashboard/admin.
- Operacoes travadas geram alerta.

#### Testes obrigatorios

- `Payout_WhenProviderTimeout_ShouldRetryWithSameIdempotencyKey`
- `Refund_WhenProviderSucceedsButDbFails_ShouldBeRecoveredByReconciliation`
- `SplitReversal_WhenLedgerFails_ShouldRemainRetryable`
- `FinancialOperation_WhenMaxRetriesExceeded_ShouldCreateReconciliationIssue`

### 3. Reconciliacao operacional completa

#### Problema

Reconciliacao nao deve ser apenas diagnostico tecnico. Precisa virar ferramenta operacional.

#### O que implementar

Adicionar/validar checks:

- `SPLIT_CLEARING_NON_ZERO_WITH_NO_PENDING_WORK`
- `SPLIT_TRANSFER_PROCESSING_TOO_OLD`
- `SPLIT_TRANSFER_RESERVED_TOO_OLD`
- `LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY_KEY`
- `SELLER_WALLET_NEGATIVE_UNEXPECTED`
- `PAYOUT_PROVIDER_SUCCESS_LEDGER_MISSING`
- `REFUND_PROVIDER_SUCCESS_LEDGER_MISSING`
- `PROVIDER_FEE_ACTUAL_DIFFERS_FROM_ESTIMATE`
- `PLATFORM_MARGIN_NEGATIVE`
- `DIRECT_CHARGE_WITH_SPLIT_LEGACY`

Criar status de issue:

```text
OPEN
ACKNOWLEDGED
IN_PROGRESS
RESOLVED
IGNORED_WITH_REASON
```

Adicionar endpoint/admin para:

- listar issues;
- filtrar por severidade;
- marcar como reconhecida;
- resolver com justificativa;
- vincular correcao manual.

#### Criterios de aceite

- Todo saldo preso vira issue.
- Toda divergencia provider/ledger vira issue.
- Issue critica gera alerta.
- Operador consegue ver e resolver.
- Historico fica auditavel.

### 4. Observabilidade de dinheiro real

#### Problema

Para producao, logs nao bastam. Precisa de metricas e alertas acionaveis.

#### O que implementar

Metricas obrigatorias:

```text
split_clearing_balance_cents
split_transfer_stuck_total
split_transfer_retry_total
split_distribute_idempotency_hit_total
refund_retry_total
payout_retry_total
payout_failed_total
refund_failed_total
reconciliation_critical_issues_total
provider_cost_mismatch_total
platform_margin_negative_total
webhook_duplicate_total
webhook_invalid_signature_total
provider_circuit_open_total
```

Alertas obrigatorios:

- `SPLIT_CLEARING` nao zero por mais de X minutos sem pending splits.
- `SplitTransfer PROCESSING/RESERVED` por mais de X minutos.
- refund provider success sem ledger.
- payout provider success sem ledger.
- margem negativa acima de limite.
- aumento de webhook duplicado/invalid signature.
- circuit breaker aberto por provider.

#### Criterios de aceite

- Dashboards Grafana/Prometheus mostram saude financeira.
- Alertas tem severidade e runbook.
- Cada alerta aponta para acao operacional.

## Parte 2 - Completar oferta para seller

### 5. Assinaturas recorrentes maduras com cobranca automatica real

#### Estado esperado

Seller deve poder vender assinaturas com:

- plano recorrente;
- ciclo mensal/anual/custom;
- primeira cobranca;
- cobranca automatica futura;
- retry/dunning;
- cancelamento;
- pausa;
- troca de plano;
- historico de cobrancas;
- webhooks de status;
- recibos/invoices por ciclo.

#### O que implementar

Modelo sugerido:

```text
Subscription
SubscriptionPlan
SubscriptionItem
SubscriptionCycle
SubscriptionInvoice
SubscriptionPaymentAttempt
SubscriptionEvent
```

Campos essenciais:

- `SellerId`
- `CustomerId`
- `PaymentMethod`
- `Provider`
- `Amount`
- `Currency`
- `Interval`
- `NextBillingAt`
- `Status`
- `CanceledAt`
- `TrialEndsAt`
- `RetryCount`
- `LastPaymentAttemptId`

Fluxo:

1. Criar assinatura.
2. Criar primeira tentativa de pagamento.
3. Se pago, marcar ciclo como pago e agendar proximo.
4. Job diario/hora processa `NextBillingAt`.
5. Falha dispara dunning.
6. Max dunning cancela ou marca past_due.

#### Integracoes

Cartao:

- Stripe PaymentMethod salvo/tokenizado.
- Nunca armazenar PAN/CVV.

Pix:

- Pix recorrente so se houver suporte real: Pix automatico/OpenPix/Woovi, ou cobrancas recorrentes manuais por ciclo.
- Se for manual, chamar de "cobranca recorrente por Pix" e nao "debito automatico".

#### Criterios de aceite

- Assinatura cobra automaticamente cartao no ciclo.
- Pix recorrente cria cobranca por ciclo ou usa Pix Automatico se integrado.
- Falha entra em dunning.
- Cancelamento impede cobrancas futuras.
- Refund de ciclo ajusta invoice/receipt.
- Split funciona em cobrancas recorrentes.

#### Testes obrigatorios

- `Subscription_FirstChargeSuccess_ShouldActivate`
- `Subscription_RenewalDue_ShouldCreatePaymentAttempt`
- `Subscription_CardRenewalSuccess_ShouldScheduleNextCycle`
- `Subscription_CardRenewalFailure_ShouldEnterDunning`
- `Subscription_MaxDunningFailed_ShouldMarkPastDueOrCanceled`
- `Subscription_Cancel_ShouldStopFutureBilling`
- `Subscription_WithSplit_ShouldCreateSplitTransfersForCycle`

### 6. NF/NFS-e opcional por seller

#### Objetivo

Seller deve poder habilitar/desabilitar emissao fiscal. A plataforma nao deve obrigar todos os sellers a emitir NF pelo sistema.

#### O que implementar

Adicionar configuracao fiscal por seller:

```text
SellerFiscalSettings
- SellerId
- InvoiceEnabled
- Provider: WOOVI | OTHER | NONE
- IssueOn: PAYMENT_CAPTURED | MANUAL | NEVER
- ServiceCode
- MunicipalTaxCode
- CompanyLegalName
- Cnpj
- MunicipalRegistration
- TaxRegime
- Address
- RpsSeries
- Environment: SANDBOX | PRODUCTION
```

Endpoints:

- `GET /sellers/{id}/fiscal-settings`
- `PUT /sellers/{id}/fiscal-settings`
- `POST /transactions/{id}/invoice`
- `GET /transactions/{id}/invoice`
- `POST /transactions/{id}/invoice/cancel`

Fluxo automatico:

1. Pagamento capturado.
2. Verificar `SellerFiscalSettings.InvoiceEnabled`.
3. Se enabled e `IssueOn = PAYMENT_CAPTURED`, criar invoice job.
4. Job emite no provider fiscal.
5. Salvar status, numero, XML, PDF, chave/verificacao.

#### Estados da NF

```text
NOT_REQUESTED
PENDING
ISSUED
FAILED_RETRYABLE
FAILED_FINAL
CANCELED
```

#### Criterios de aceite

- Seller pode habilitar/desabilitar.
- Seller sem fiscal enabled nao emite NF.
- Seller com fiscal enabled emite automaticamente ou manualmente.
- Falha fiscal nao quebra captura de pagamento.
- XML/PDF ficam acessiveis.
- Cancelamento de NF e auditado.

#### Testes obrigatorios

- `Invoice_ShouldNotIssue_WhenSellerFiscalDisabled`
- `Invoice_ShouldIssueOnCapture_WhenSellerFiscalEnabled`
- `Invoice_ShouldRetry_WhenProviderTemporaryFailure`
- `Invoice_ShouldStorePdfAndXml`
- `Invoice_ShouldCancel_WhenAllowed`

### 7. Invoices fiscais completas

#### Objetivo

Separar "invoice fiscal/NF" de "invoice comercial/cobranca".

O seller precisa:

- ver invoice da venda;
- ver status fiscal;
- baixar PDF/XML quando houver;
- enviar para cliente;
- cancelar quando permitido.

#### O que implementar

Entidade:

```text
FiscalInvoice
- Id
- TenantId
- SellerId
- TransactionId
- CustomerId
- Provider
- ProviderInvoiceId
- Status
- Amount
- ServiceAmount
- TaxAmount
- Number
- VerificationCode
- PdfUrl or PdfStorageKey
- XmlUrl or XmlStorageKey
- ErrorCode
- ErrorMessage
- IssuedAt
- CanceledAt
```

Para invoice comercial:

```text
CommercialInvoice
- Id
- TransactionId
- SellerId
- CustomerId
- Items
- Amount
- Status
- DueDate
- PaidAt
```

#### Criterios de aceite

- Uma transacao pode ter invoice comercial e fiscal.
- NF cancelada nao apaga historico.
- Refund parcial atualiza estado comercial e pode orientar cancelamento/ajuste fiscal.
- Admin consegue listar falhas fiscais.

### 8. Recibos robustos para todos os metodos

#### Objetivo

Todo pagamento deve gerar comprovante/recibo consistente, independente do provider.

#### Metodos

- Pix OpenPix;
- cartao Stripe;
- debito Stripe;
- boleto Stripe;
- Apple Pay/Google Pay via Stripe;
- refund;
- payout para seller;
- split recebido.

#### O que implementar

Entidade:

```text
Receipt
- Id
- TenantId
- SellerId
- TransactionId
- Type: PAYMENT | REFUND | PAYOUT | SPLIT_RECEIVED | CHARGEBACK
- Provider
- ProviderReceiptId
- Status
- Amount
- Currency
- PdfStorageKey
- HtmlStorageKey
- PublicUrl
- CreatedAt
```

Servico:

- gerar recibo interno em HTML/PDF;
- anexar recibo provider quando existir;
- permitir download;
- enviar por email/webhook.

OpenPix:

- usar receipt nativo quando aplicavel;
- mapear corretamente tipos `pix-in`, `pix-out`, `pix-refund`, se exigido pela API.

Stripe:

- usar charge receipt_url quando existir;
- gerar recibo interno se Stripe nao fornecer para o caso.

#### Criterios de aceite

- Toda transacao capturada tem recibo.
- Todo refund tem recibo.
- Todo payout tem comprovante.
- Seller consegue baixar.
- Cliente pode receber link publico seguro.
- Recibo nao vaza dados de outro tenant.

#### Testes obrigatorios

- `Receipt_ShouldGenerate_ForStripeCardPayment`
- `Receipt_ShouldGenerate_ForOpenPixPayment`
- `Receipt_ShouldGenerate_ForRefund`
- `Receipt_ShouldGenerate_ForPayout`
- `Receipt_ShouldRejectCrossTenantAccess`

### 9. Simulador de split antes da venda

#### Objetivo

Antes de criar uma transacao, seller/plataforma deve conseguir simular:

- valor bruto;
- taxa plataforma;
- custo provider estimado;
- margem plataforma;
- valor de cada recipient;
- residual do primary seller;
- rounding;
- limites do plano;
- erros de configuracao.

#### Endpoint sugerido

```http
POST /api/v1/splits/simulate
```

Request:

```json
{
  "sellerId": "...",
  "amount": 1000.00,
  "paymentType": "PIX",
  "installments": 1,
  "splitRuleId": "...",
  "splits": [
    { "sellerId": "...", "amount": 100.00 },
    { "sellerId": "...", "percentage": 20.0 }
  ]
}
```

Response:

```json
{
  "grossAmount": 1000.00,
  "platformFee": 15.00,
  "providerCostEstimate": 0.80,
  "platformMarginEstimate": 14.20,
  "netAmount": 985.00,
  "recipients": [
    { "sellerId": "...", "amount": 100.00, "type": "RECIPIENT_SHARE" }
  ],
  "primaryResidual": {
    "sellerId": "...",
    "amount": 885.00
  },
  "warnings": []
}
```

#### Criterios de aceite

- Simulacao usa mesma logica do split real.
- Simulacao nao cria transacao.
- Valida limites de plano.
- Mostra erros antes do pagamento.
- Testes com rounding.

### 10. Split por produto/item

#### Objetivo

Permitir que uma venda com multiplos itens distribua valores conforme produto, SKU, categoria ou regra.

#### O que implementar

Entidades:

```text
TransactionItem
- Id
- TransactionId
- ProductId
- Description
- Quantity
- UnitAmount
- TotalAmount
- SellerId
- SplitRuleId

ProductSplitRule
- ProductId
- SplitRuleId
```

Fluxo:

1. Criar transacao com itens.
2. Cada item resolve seller/regra.
3. Sistema agrupa destinatarios por seller.
4. Aplica rounding por transacao.
5. Gera `SplitTransfer` agregado ou detalhado por item.

Decisao necessaria:

- SplitTransfer por seller agregado: mais simples.
- SplitTransfer por item: mais auditavel.

Recomendacao:

- criar `SplitAllocation` por item;
- criar `SplitTransfer` agregado por seller;
- manter relacao para auditoria.

#### Criterios de aceite

- Produto A pode splitar diferente do Produto B.
- Soma dos itens bate com gross amount.
- Soma das alocacoes bate com net amount.
- Refund parcial por item reverte apenas o item afetado, se informado.

#### Testes obrigatorios

- `ItemSplit_ShouldAllocateByProductRule`
- `ItemSplit_ShouldAggregateSameRecipient`
- `ItemSplit_ShouldHandleRounding`
- `ItemRefund_ShouldReverseOnlyAffectedItem`

### 11. Portal seller completo

#### Objetivo

Se hoje o sistema e mais API/admin, criar portal seller para reduzir operacao manual.

#### Funcionalidades minimas

Dashboard:

- saldo disponivel;
- saldo futuro;
- vendas do dia/mes;
- refunds;
- chargebacks;
- payouts pendentes;
- alertas.

Transacoes:

- listar;
- filtrar;
- detalhe;
- recibo;
- refund total/parcial;
- status fiscal;
- timeline.

Payouts:

- solicitar saque;
- ver historico;
- ver taxas;
- comprovante.

Split:

- ver splits recebidos;
- ver regras;
- simular split;
- relatorio por recipient.

Fiscal:

- habilitar/desabilitar NF;
- configurar dados fiscais;
- listar invoices;
- baixar PDF/XML.

Assinaturas:

- listar assinaturas;
- cancelar/pausar;
- ver ciclos;
- ver tentativas de cobranca.

Configuracoes:

- API keys;
- webhooks;
- dados cadastrais;
- 2FA;
- conta bancaria/Pix key.

#### Criterios de aceite

- Seller consegue operar sem pedir suporte para tarefas comuns.
- Portal respeita tenant isolation.
- Acoes sensiveis exigem 2FA ou reautenticacao.
- Logs de auditoria para alteracoes criticas.

## Parte 3 - Checklist de producao

### 12. Garantias para ir a producao

Antes de producao aberta:

- [ ] Split idempotente no ledger.
- [ ] Refund parcial sequencial validado.
- [ ] Payout com retry seguro.
- [ ] Reconciliacao com issues operacionais.
- [ ] NF opcional por seller.
- [ ] Recibos para todos os metodos.
- [ ] Assinaturas maduras ou explicitamente fora do escopo comercial.
- [ ] Portal seller ou documentacao/API suficiente para beta.
- [ ] Observabilidade com alertas.
- [ ] Runbooks.
- [ ] Backups.
- [ ] Rollback de migrations.
- [ ] Segredos em env/secret manager.
- [ ] Webhooks live validados.
- [ ] Stripe live validado.
- [ ] OpenPix live validado.
- [ ] LGPD/termos/politica de privacidade revisados.
- [ ] Limites de transacao e volume no go-live.

### 13. Runbooks obrigatorios

Criar em `docs/runbooks/`:

- `split-clearing-residual.md`
- `split-transfer-stuck.md`
- `refund-provider-success-ledger-failed.md`
- `payout-provider-success-ledger-failed.md`
- `provider-cost-mismatch.md`
- `negative-platform-margin.md`
- `webhook-duplicates.md`
- `chargeback-lost.md`
- `nf-issue-failed.md`
- `seller-payout-support.md`

Cada runbook deve ter:

- sintoma;
- alerta relacionado;
- queries SQL;
- impacto;
- acao imediata;
- correcao definitiva;
- como validar;
- quando escalar.

## Parte 4 - Ordem recomendada de execucao

### Fase 1 - Bloqueadores financeiros

1. Ledger idempotente para split distribute/reversal.
2. Corrigir retry de split sem duplicar.
3. Corrigir payout/refund retry com idempotency key.
4. Reconciliacao operacional e alerts.

Sem essa fase, nao chamar de marketplace pronto.

### Fase 2 - Oferta seller essencial

5. Recibos robustos.
6. NF/NFS-e opcional.
7. Invoices fiscais.
8. Portal seller basico.

Sem essa fase, produto ainda exige suporte manual demais.

### Fase 3 - Produto avancado

9. Assinaturas recorrentes maduras.
10. Simulador de split.
11. Split por produto/item.
12. Portal seller completo.

Essa fase diferencia o produto, mas nao deve vir antes da seguranca financeira.

## Definicao final de "completo"

O sistema so deve ser chamado de completo quando:

- consegue cobrar por todos os metodos prometidos;
- consegue dividir dinheiro sem duplicar ou perder;
- consegue estornar parcial e total corretamente;
- consegue sacar com compensacao segura;
- consegue emitir comprovantes;
- consegue emitir NF opcional;
- consegue reconciliar provider versus ledger;
- consegue alertar divergencias;
- consegue dar autonomia ao seller;
- consegue provar tudo com auditoria.

Frase final para guiar o Claude Code:

```text
Nao basta a transacao aprovar. O sistema precisa sobreviver a webhook duplicado,
queda de servidor, retry, taxa divergente, refund parcial, chargeback, payout falho
e pergunta do seller pedindo comprovante.
```

