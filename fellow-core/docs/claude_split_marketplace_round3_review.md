# Relatorio para Claude Code - Revisao Round 3 do Split Marketplace

Data da revisao: 2026-05-04  
Projeto: Fellow Pay  
Area revisada: SPLIT_CLEARING, split marketplace, lucro liquido, custos de provider, direct charge, refunds e reconciliacao.

## Resumo executivo

Os testes reportados pelo Claude estao verdes:

- Domain: 312 passed
- Application: 644 passed, 16 skipped
- Integration: 260 passed
- Total: 1.216 passed, 16 skipped, 0 failed

Mesmo assim, a revisao manual encontrou problemas que ainda impedem classificar o sistema como 10/10 em split marketplace. Os problemas nao sao de compilacao; sao falhas de comportamento, idempotencia, modelagem de ledger e cobertura de testes.

Status atual estimado:

- Lucro / margem: perto de 10/10, assumindo que os fluxos de margem ja estao cobertos.
- Custo de provider: perto de 10/10, assumindo conciliacao de custo real versus estimado.
- SPLIT_CLEARING: 8/10 por causa de idempotencia e casos de residual.
- Split marketplace avancado: 8/10 por causa de direct charge, primary residual, retries e refunds parciais sequenciais.
- Reconciliacao de split: 8/10 por falso positivo com `IsPrimaryShare` e falta de checks especificos para marker preso.

Conclusao: os fixes da rodada 2 melhoraram o sistema, mas ainda existem cinco pontos que precisam ser corrigidos antes de chamar de 10/10.

## Regras de implementacao para esta rodada

Claude deve corrigir os pontos abaixo sem mascarar problemas com testes superficiais. Cada fix precisa ter:

- alteracao de codigo;
- teste unitario ou de fluxo que falhe antes e passe depois;
- cobertura de caso negativo;
- nenhuma regressao nos testes existentes;
- explicacao curta da decisao de modelagem.

Nao basta aumentar asserts de chamadas com `Arg.Any`. Os testes precisam validar valores e estados finais relevantes: status do `SplitTransfer`, saldo distribuido, chamadas ao ledger, existencia de marker correto e comportamento em retry.

## Finding 1 - CRITICAL: marker de idempotencia salvo antes do ledger pode pular distribuicao que nunca aconteceu

### Severidade

Critical.

### Arquivos envolvidos

- `src/FellowCore.Infrastructure/Workers/Processors/SplitProcessor.cs`
- `src/FellowCore.Infrastructure/Repositories/SplitTransferRepository.cs`
- `src/FellowCore.Domain/Entities/SplitTransfer.cs`
- `src/FellowCore.Domain/Interfaces/ISplitTransferRepository.cs`

### Evidencia no codigo

No processor, o marker e criado e salvo antes do ledger:

```csharp
var transfer = transferResult.Value;
transfer.Reserve();
splitTransferRepository.Add(transfer);
await splitTransferRepository.SaveChangesAsync(); // Persist marker before ledger

await ledgerService.DistributeFromClearingAsync(...);
```

Trecho atual aproximado:

- `SplitProcessor.cs:143-160`

No retry, qualquer `SplitTransfer` existente que nao esteja `FAILED` faz o processor pular:

```csharp
var existing = await splitTransferRepository.GetByTransactionAndRecipientAsync(
    transaction.TenantId, transaction.Id, sellerId);

if (existing != null && existing.Status != SplitTransferStatus.FAILED)
{
    split.MarkAsPaid();
    continue;
}
```

Trecho atual aproximado:

- `SplitProcessor.cs:131-140`

### Cenario de falha

1. Transacao capturada com split.
2. `SPLIT_CLEARING` foi creditado com `NetAmount`.
3. `SplitProcessor` cria `SplitTransfer` com status `RESERVED`.
4. `SplitProcessor` chama `SaveChangesAsync`.
5. Processo cai antes de executar `DistributeFromClearingAsync`.
6. Novo retry carrega a mesma transacao com split pendente.
7. O repositorio encontra o `SplitTransfer RESERVED`.
8. O processor interpreta como ja processado, marca o split como `PAID` e pula a distribuicao.
9. Dinheiro fica preso no `SPLIT_CLEARING`.
10. Seller nao recebe, mas o sistema passa a acreditar que o split foi pago.

### Impacto

- Perda operacional: seller nao recebe saldo.
- Ledger inconsistente: clearing com saldo residual.
- Status enganoso: split pode virar `PAID` sem movimentacao no ledger.
- Reconciliacao pode detectar residual, mas tarde demais e sem auto-correcao.

### Correcao esperada

O `SplitTransfer` precisa distinguir claramente:

- marker criado mas ledger ainda nao executado;
- ledger em execucao;
- ledger concluido;
- falha recuperavel.

Opcao recomendada:

1. Criar marker como `PENDING`.
2. Salvar marker.
3. Mudar para `PROCESSING` antes de chamar ledger.
4. Chamar `DistributeFromClearingAsync`.
5. So depois de ledger concluido, marcar `PAID`.
6. No retry:
   - `PAID`: pular;
   - `RESERVED` se usado como "ledger ja reservado/concluido": pular somente se houver evidencia ledger;
   - `PENDING` ou `PROCESSING` antigo/stale: tentar retomar ou marcar `FAILED` para retry;
   - `FAILED`: criar nova tentativa ou reusar o mesmo marker com transicao valida.

Melhor ainda: tornar a operacao de ledger idempotente com uma chave unica por movimento:

```text
split:{transactionId}:{recipientSellerId}:recipient
split:{transactionId}:{primarySellerId}:primary
```

E validar no ledger se ja existe entry `SPLIT_DISTRIBUTE` com a mesma chave antes de debitar clearing novamente.

### Criterio de aceite

Deve existir teste simulando crash entre marker e ledger:

1. Primeira execucao salva `SplitTransfer`, mas `DistributeFromClearingAsync` nao chega a completar.
2. Segunda execucao nao deve marcar como pago sem ledger.
3. Segunda execucao deve executar a distribuicao ou deixar status explicitamente recuperavel.
4. No final, seller recebe exatamente uma vez.
5. `SPLIT_CLEARING` nao fica com saldo residual indevido.

### Teste minimo recomendado

Nome sugerido:

```csharp
ProcessSplitsForTransactionAsync_WhenMarkerExistsButLedgerNotCompleted_ShouldRetryDistribution()
```

Esse teste deve montar:

- uma transacao capturada;
- split pendente;
- `SplitTransfer` existente com status `PENDING` ou `PROCESSING` sem ledger;
- assert de que `DistributeFromClearingAsync` e chamado;
- assert de que transfer vira `PAID` somente depois do ledger.

## Finding 2 - HIGH: `IsPrimaryShare` existe no banco, mas o repositorio nao usa no lookup

### Severidade

High.

### Arquivos envolvidos

- `src/FellowCore.Domain/Entities/SplitTransfer.cs`
- `src/FellowCore.Domain/Interfaces/ISplitTransferRepository.cs`
- `src/FellowCore.Infrastructure/Repositories/SplitTransferRepository.cs`
- `src/FellowCore.Infrastructure/Workers/Processors/SplitProcessor.cs`
- `src/FellowCore.Infrastructure/Database/Migrations/20260504172056_AddSplitTransferIsPrimaryShare.cs`

### Evidencia no codigo

A migration cria indice unico com `IsPrimaryShare`:

```csharp
columns: new[] { "TenantId", "TransactionId", "RecipientSellerId", "IsPrimaryShare" },
unique: true,
filter: "\"Status\" != 4"
```

Trecho:

- `20260504172056_AddSplitTransferIsPrimaryShare.cs:24-29`

Mas o repositorio ainda busca sem `IsPrimaryShare`:

```csharp
public async Task<SplitTransfer?> GetByTransactionAndRecipientAsync(
    Guid tenantId,
    Guid transactionId,
    Guid recipientSellerId)
{
    return await context.SplitTransfers
        .FirstOrDefaultAsync(st => st.TenantId == tenantId &&
                                   st.TransactionId == transactionId &&
                                   st.RecipientSellerId == recipientSellerId);
}
```

Trecho:

- `SplitTransferRepository.cs:20-25`

O processor chama esse mesmo metodo para recipient normal e para primary residual:

```csharp
var existing = await splitTransferRepository.GetByTransactionAndRecipientAsync(
    transaction.TenantId, transaction.Id, sellerId);
```

```csharp
var existingPrimary = await splitTransferRepository.GetByTransactionAndRecipientAsync(
    transaction.TenantId, transaction.Id, primarySellerId);
```

Trechos:

- `SplitProcessor.cs:131-133`
- `SplitProcessor.cs:199-201`

### Cenario de falha

1. Seller primario tambem aparece como recebedor em uma regra de split.
2. Processor cria `SplitTransfer` normal para esse seller com `IsPrimaryShare = false`.
3. Depois calcula `primaryShare = netAmount - totalDistributed`.
4. Ao tentar criar o residual do primary, chama lookup sem `IsPrimaryShare`.
5. O repositorio encontra o transfer normal do mesmo seller.
6. Processor conclui que o primary residual ja existe.
7. Residual nao e distribuido.
8. Parte do dinheiro fica presa no `SPLIT_CLEARING`.

### Impacto

- Primary seller pode nao receber residual.
- Clearing fica com saldo residual.
- O indice novo no banco nao resolve o bug porque a query nao usa o campo novo.

### Correcao esperada

Alterar contrato do repositorio para aceitar `isPrimaryShare`:

```csharp
Task<SplitTransfer?> GetByTransactionAndRecipientAsync(
    Guid tenantId,
    Guid transactionId,
    Guid recipientSellerId,
    bool isPrimaryShare = false);
```

Implementar filtro:

```csharp
.FirstOrDefaultAsync(st =>
    st.TenantId == tenantId &&
    st.TransactionId == transactionId &&
    st.RecipientSellerId == recipientSellerId &&
    st.IsPrimaryShare == isPrimaryShare);
```

No processor:

```csharp
// recipient normal
GetByTransactionAndRecipientAsync(..., isPrimaryShare: false)

// primary residual
GetByTransactionAndRecipientAsync(..., isPrimaryShare: true)
```

### Criterio de aceite

Caso em que primary seller tambem e recipient deve:

- criar dois `SplitTransfer` distintos:
  - um com `IsPrimaryShare = false`;
  - um com `IsPrimaryShare = true`;
- distribuir os dois valores corretos;
- nao gerar duplicidade indevida;
- nao deixar saldo no clearing.

### Teste minimo recomendado

Nome sugerido:

```csharp
ProcessSplitsForTransactionAsync_WhenPrimarySellerIsAlsoRecipient_ShouldCreateRecipientAndPrimaryTransfers()
```

Asserts obrigatorios:

- `DistributeFromClearingAsync` chamado para o primary como recipient;
- `DistributeFromClearingAsync` chamado para o primary residual, se houver residual;
- dois markers com `IsPrimaryShare` diferentes;
- total distribuido = `transaction.NetAmount`.

## Finding 3 - HIGH: bloqueio de DIRECT_CHARGE esta amplo demais e pode bloquear Pix/OpenPix com split

### Severidade

High.

### Arquivo envolvido

- `src/FellowCore.Application/Modules/Transactions/Services/TransactionService.cs`

### Evidencia no codigo

O bloqueio atual:

```csharp
if (resolvedSplits.Count > 0 && tenant.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE)
    throw new BusinessException("Split.DirectChargeUnsupported",
        "Splits não são suportados no modo Direct Charge. Use Destination Charge para transações com split.");
```

Trecho:

- `TransactionService.cs:177-181`

Esse codigo nao verifica se a transacao e Stripe. Como o tenant pode estar configurado com `StripeChargeMode.DIRECT_CHARGE` para cartao, mas o pagamento ser `PIX` via OpenPix, esse bloqueio pode impedir Pix com split indevidamente.

### Contexto de produto

O usuario ja deixou claro:

- Pix e OpenPix/Woovi.
- Cartao e Stripe.

Portanto, uma config de Stripe direct charge nao deveria bloquear Pix OpenPix, porque OpenPix nao usa Stripe direct charge.

### Cenario de falha

1. Tenant usa Stripe direct charge para cartoes.
2. Seller cria transacao Pix com OpenPix.
3. Transacao inclui split.
4. `resolvedSplits.Count > 0`.
5. `tenant.Config.StripeChargeMode == DIRECT_CHARGE`.
6. Sistema rejeita Pix com split, mesmo Pix nao sendo Stripe.

### Impacto

- Bloqueia feature valida de Pix marketplace.
- Pode quebrar checkout Pix de sellers em tenants que usam direct charge para cartao.
- Mistura regra de rail Stripe com rail OpenPix.

### Correcao esperada

O bloqueio deve considerar o rail/provider:

```csharp
var isStripeRail = rail.RailType is PaymentRailType.STRIPE_CARD or PaymentRailType.STRIPE_BOLETO;

if (resolvedSplits.Count > 0 &&
    isStripeRail &&
    tenant.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE)
{
    throw new BusinessException(...);
}
```

Ou, se a regra for por provider:

```csharp
if (resolvedSplits.Count > 0 &&
    provider == PaymentProvider.STRIPE &&
    tenant.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE)
```

### Criterio de aceite

- Stripe card com direct charge + split deve falhar.
- Stripe boleto com direct charge + split deve falhar, se boleto Stripe usa direct charge.
- OpenPix Pix com tenant Stripe direct charge + split deve passar.
- Destination charge + split deve passar.

### Testes minimos recomendados

```csharp
CreateAsync_ShouldThrow_WhenStripeCardSplitsWithDirectChargeMode()
```

```csharp
CreateAsync_ShouldAllow_WhenOpenPixPixSplitsAndStripeDirectChargeMode()
```

O teste existente `CreateAsync_ShouldThrow_WhenSplitsWithDirectChargeMode` usa `PaymentType.PIX`, entao ele esta validando o comportamento errado se Pix for OpenPix. Esse teste precisa ser corrigido.

## Finding 4 - HIGH: reembolso parcial sequencial pode superdebitar o seller primario

### Severidade

High.

### Arquivos envolvidos

- `src/FellowCore.Application/Modules/Webhooks/Services/WebhooksService.cs`
- `src/FellowCore.Domain/Entities/SplitTransfer.cs`

### Evidencia no codigo

No calculo do debito do primary seller para refund Stripe:

```csharp
decimal totalSplitAmount = activeSplitTransfers
    .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID)
    .Sum(t => t.Amount);
```

Trecho:

- `WebhooksService.cs:371-374`

No refund OpenPix:

```csharp
decimal totalSplitAmount = activeSplitTransfers
    .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID)
    .Sum(t => t.Amount);
```

Trecho:

- `WebhooksService.cs:895-898`

Mas a reversao de recipients considera parcialmente revertidos:

```csharp
var reversibleTransfers = transfers
    .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID or SplitTransferStatus.PARTIALLY_REVERSED)
    .Where(t => t.RemainingAmount > 0)
    .ToList();
```

Trecho:

- `WebhooksService.cs:1123-1126`

### Cenario de falha

Exemplo:

- Transacao: gross 1000
- Net: 970
- Split recipients total: 600
- Primary residual: 370

Primeiro refund parcial:

- Refund gross: 500
- Proportional net refund: 485
- Recipients devolvem parte.
- Transfers viram `PARTIALLY_REVERSED`.

Segundo refund parcial:

- O calculo do primary debit ignora `PARTIALLY_REVERSED`.
- `totalSplitAmount` pode ficar 0 ou menor que deveria.
- `primaryRatio` aumenta indevidamente.
- Primary seller paga mais do que sua responsabilidade real.
- Recipients ainda sao debitados pela reversao proporcional restante.
- Total debitado dos sellers pode exceder o net refund proporcional.

### Impacto

- Superdebito do seller primario.
- Injustica financeira entre sellers.
- Possivel saldo negativo indevido em wallet.
- Reconciliacao dificil, porque os movimentos individualmente parecem validos.

### Correcao esperada

O calculo do debito do primary deve usar saldo remanescente dos splits, nao apenas status inicial.

Em vez de:

```csharp
.Where(t => t.Status is RESERVED or PAID)
.Sum(t => t.Amount)
```

Usar algo como:

```csharp
decimal totalSplitRemaining = activeSplitTransfers
    .Where(t => t.Status is SplitTransferStatus.RESERVED
        or SplitTransferStatus.PAID
        or SplitTransferStatus.PARTIALLY_REVERSED)
    .Sum(t => t.RemainingAmount);
```

E revisar a formula:

```csharp
primaryRemaining = transaction.NetAmount.Value - totalSplitRemaining;
primaryRatio = primaryRemaining / transaction.NetAmount.Value;
```

Mas cuidado: em refunds sequenciais, o primary tambem precisa ter alguma forma de rastrear quanto ja foi debitado do residual primario, ou o calculo com base apenas em `SplitTransfer` dos recipients pode continuar errado.

Recomendacao mais robusta:

- Tratar o primary residual como `SplitTransfer IsPrimaryShare = true`.
- Incluir esse transfer na mesma logica de `RemainingAmount`.
- No refund, nao calcular primary debit separadamente por formula.
- Reverter todos os `SplitTransfer` ativos, incluindo primary residual e recipients, proporcionalmente pelo `RemainingAmount`.
- Assim, todos os sellers sao debitados pela mesma logica e o sistema evita divergencia.

Fluxo recomendado:

1. Toda distribuicao de split gera `SplitTransfer`, inclusive primary residual.
2. Refund carrega todos os transfers ativos:
   - recipients;
   - primary residual.
3. Calcula `proportionalRefund`.
4. Distribui reversao proporcional por `RemainingAmount`.
5. Debita cada seller por `ReturnToClearingAsync`.
6. Atualiza `ReversedAmount`.
7. Drena clearing para payout/refund.

Isso elimina o bloco separado de `DebitSellerAsync` para primary quando a transacao tem split.

### Criterio de aceite

Com dois refunds parciais sequenciais:

- soma debitada de todos os sellers no primeiro refund = net proporcional do primeiro refund;
- soma debitada de todos os sellers no segundo refund = net proporcional do segundo refund;
- nenhum seller e debitado acima de seu `RemainingAmount`;
- todos os transfers chegam a `REVERSED` depois de refund total acumulado;
- `RemainingAmount` final = 0 para todos;
- clearing final = 0.

### Testes minimos recomendados

```csharp
PartialRefund_Twice_ShouldDebitOnlyRemainingAmounts()
```

```csharp
FullRefund_AfterPartialRefund_ShouldReverseOnlyRemainingAmounts()
```

```csharp
Refund_WithPrimaryResidualTransfer_ShouldReversePrimaryThroughSplitTransferFlow()
```

## Finding 5 - MEDIUM/HIGH: reconciliacao de duplicidade nao considera `IsPrimaryShare`

### Severidade

Medium/High.

### Arquivo envolvido

- `src/FellowCore.Application/Modules/Reconciliation/Services/ReconciliationService.cs`

### Evidencia no codigo

A reconciliacao agrupa duplicidade somente por recipient:

```csharp
var duplicateRecipients = transfers
    .Where(t => t.Status != SplitTransferStatus.FAILED)
    .GroupBy(t => t.RecipientSellerId)
    .Where(g => g.Count() > 1)
    .ToList();
```

Trecho:

- `ReconciliationService.cs:823-828`

Mas agora existe `IsPrimaryShare`, e o mesmo seller pode ter:

- um transfer normal como recipient (`IsPrimaryShare = false`);
- um transfer de residual primario (`IsPrimaryShare = true`).

Isso nao e duplicidade; e comportamento esperado.

### Impacto

- Falso positivo `SPLIT_DUPLICATE_CREDIT`.
- Alertas criticos incorretos.
- Operacao pode perder confianca na reconciliacao.

### Correcao esperada

Agrupar por `(RecipientSellerId, IsPrimaryShare)`:

```csharp
.GroupBy(t => new { t.RecipientSellerId, t.IsPrimaryShare })
```

Ou, se for adotado um campo mais explicito:

```csharp
SplitTransferKind = RecipientShare | PrimaryResidual
```

Agrupar por `(RecipientSellerId, Kind)`.

### Criterio de aceite

- Mesmo seller com um recipient transfer e um primary residual nao gera `SPLIT_DUPLICATE_CREDIT`.
- Dois transfers ativos do mesmo tipo para mesmo seller continuam gerando issue.
- Transfers `FAILED` continuam ignorados.

### Testes minimos recomendados

```csharp
Reconciliation_ShouldNotFlagDuplicate_WhenSameSellerHasRecipientAndPrimaryShare()
```

```csharp
Reconciliation_ShouldFlagDuplicate_WhenSameSellerHasTwoRecipientShares()
```

## Finding 6 - MEDIUM: `SplitTransfer` criado para distribuicao bem-sucedida nao vira `PAID`

### Severidade

Medium.

### Arquivo envolvido

- `src/FellowCore.Infrastructure/Workers/Processors/SplitProcessor.cs`

### Evidencia no codigo

O transfer e criado, recebe `Reserve()`, e depois do ledger o split da transacao vira `PAID`:

```csharp
var transfer = transferResult.Value;
transfer.Reserve();
splitTransferRepository.Add(transfer);
await splitTransferRepository.SaveChangesAsync();

await ledgerService.DistributeFromClearingAsync(...);

split.MarkAsPaid();
```

Mas nao ha chamada visivel para:

```csharp
transfer.MarkProcessing();
transfer.MarkPaid();
```

Trecho:

- `SplitProcessor.cs:152-168`

No primary residual, tambem nao ha `MarkPaid` no `SplitTransfer` apos ledger:

```csharp
primaryTransfer.Reserve();
splitTransferRepository.Add(primaryTransfer);
await splitTransferRepository.SaveChangesAsync();

await ledgerService.DistributeFromClearingAsync(...);
```

Trecho:

- `SplitProcessor.cs:219-233`

### Impacto

- `SplitTransfer` fica `RESERVED` mesmo depois do dinheiro ser distribuido.
- Reconciliacao e dashboards podem interpretar como pendente.
- Refund ate consegue reverter `RESERVED`, mas semanticamente o status esta errado.
- Contadores de pendencias podem ficar inflados.

### Correcao esperada

Depois do ledger bem-sucedido:

```csharp
transfer.MarkProcessing();
transfer.MarkPaid();
splitTransferRepository.Update(transfer);
```

Ou ajustar a maquina de estado para permitir `RESERVED -> PAID` se esse for o fluxo real.

Hoje a entidade permite:

- `RESERVED -> PROCESSING`
- `PROCESSING -> PAID`

Entao o processor deve seguir essa sequencia ou a entidade deve ser alterada conscientemente.

### Criterio de aceite

- Transfer bem-sucedido deve terminar `PAID`.
- Transfer criado mas ledger nao executado nao deve terminar `PAID`.
- Transfer com ledger falho deve terminar `FAILED` ou estado recuperavel.

### Teste minimo recomendado

```csharp
ProcessSplitsForTransactionAsync_WhenLedgerSucceeds_ShouldMarkSplitTransferPaid()
```

## Finding 7 - MEDIUM: `Reverse` / `PartialReverse` checa Result, mas continua drenando clearing mesmo se status falhar

### Severidade

Medium.

### Arquivo envolvido

- `src/FellowCore.Application/Modules/Webhooks/Services/WebhooksService.cs`

### Evidencia no codigo

Depois de mover ledger com `ReturnToClearingAsync`, o codigo chama `Reverse` ou `PartialReverse` e loga critical se falhar:

```csharp
if (reverseResult.IsFailure)
{
    logger.LogCritical(...);
}

splitTransferRepository.Update(transfer);
totalReversed += reversalAmount;
```

Trecho:

- `WebhooksService.cs:1173-1185`

### Problema

Se a transicao falha:

- ledger ja debitou seller e creditou clearing;
- o transfer nao reflete o reversal;
- mesmo assim `totalReversed` aumenta;
- clearing e drenado;
- o sistema fica contabilmente movido, mas o marker continua inconsistente.

Talvez isso seja uma decisao consciente, mas precisa de compensacao ou issue de reconciliacao persistente, nao apenas log.

### Correcao esperada

Opcao recomendada:

- Se ledger moveu e status falhou, criar `ReconciliationIssue` ou outbox/manual task.
- Persistir algum estado de erro no transfer, por exemplo `FailureReason`.
- Nao apenas logar.

Tambem considerar prevalidar a transicao antes de chamar ledger:

```csharp
if (!SplitTransfer.IsValidTransition(transfer.Status, targetStatus))
{
    // nao mover ledger
}
```

Mas cuidado com race conditions.

### Criterio de aceite

Teste em que `PartialReverse` falha deve:

- nao drenar clearing sem controle; ou
- criar issue/alerta persistente claro;
- nao deixar falha apenas em log.

## Recomendacao de modelagem para 10/10

Para reduzir complexidade, tratar todo dinheiro distribuido via split como `SplitTransfer`, inclusive primary residual.

### Modelo sugerido

Adicionar tipo explicito:

```csharp
public enum SplitTransferKind
{
    RECIPIENT_SHARE,
    PRIMARY_RESIDUAL
}
```

Ou manter `IsPrimaryShare`, mas o ideal e um enum para evoluir no futuro.

Campos importantes:

- `TransactionId`
- `TenantId`
- `RecipientSellerId`
- `Amount`
- `ReversedAmount`
- `RemainingAmount`
- `Kind` ou `IsPrimaryShare`
- `Status`
- `LedgerReference`
- `FailureReason`
- `CreatedAt`
- `ReservedAt`
- `ProcessingAt`
- `PaidAt`
- `ReversedAt`

Indice unico:

```text
TenantId, TransactionId, RecipientSellerId, Kind
WHERE Status != FAILED
```

### Estado recomendado

```text
PENDING -> PROCESSING -> PAID -> PARTIALLY_REVERSED -> REVERSED
PENDING -> FAILED
PROCESSING -> FAILED
FAILED -> PENDING ou nova tentativa
```

Evitar usar `RESERVED` se ele nao tem significado financeiro claro.

Se `RESERVED` continuar existindo, documentar:

- `RESERVED` significa marker criado mas ledger ainda nao executado?
- ou significa ledger reservado/concluido?

Hoje o uso esta ambiguo.

## Checklist de correcao para Claude

### Obrigatorio

- [ ] Ajustar `ISplitTransferRepository.GetByTransactionAndRecipientAsync` para receber `isPrimaryShare` ou `kind`.
- [ ] Ajustar `SplitTransferRepository` para filtrar por `IsPrimaryShare`.
- [ ] Atualizar todas as chamadas do repositorio no `SplitProcessor`.
- [ ] Corrigir retry para nao pular marker sem ledger concluido.
- [ ] Marcar `SplitTransfer` como `PAID` depois de ledger bem-sucedido.
- [ ] Bloquear `DIRECT_CHARGE + split` somente para rails Stripe.
- [ ] Permitir `OpenPix Pix + split` mesmo se o tenant usa Stripe direct charge para cartao.
- [ ] Corrigir refund parcial sequencial usando `RemainingAmount`.
- [ ] Preferencialmente reverter primary residual pelo mesmo fluxo de `SplitTransfer`.
- [ ] Ajustar reconciliacao de duplicidade para considerar `IsPrimaryShare`.
- [ ] Ajustar reconciliacao de refunded para considerar `PARTIALLY_REVERSED` com `RemainingAmount > 0`.
- [ ] Criar testes que reproduzam cada bug.

### Desejavel

- [ ] Introduzir `SplitTransferKind` em vez de boolean.
- [ ] Adicionar `LedgerReference` ou idempotency key do movimento de ledger.
- [ ] Criar metodo idempotente no ledger para `SPLIT_DISTRIBUTE`.
- [ ] Criar reconciliacao especifica para marker `PENDING/PROCESSING` antigo.
- [ ] Criar metricas:
  - `split_transfer_stuck_total`
  - `split_clearing_residual_cents`
  - `split_transfer_retry_total`
  - `split_refund_partial_total`

## Suite de testes minima para aceitar 10/10

### SplitProcessor

1. `ProcessSplitsForTransactionAsync_WhenMarkerExistsButLedgerNotCompleted_ShouldRetryDistribution`
2. `ProcessSplitsForTransactionAsync_WhenLedgerSucceeds_ShouldMarkSplitTransferPaid`
3. `ProcessSplitsForTransactionAsync_WhenPrimarySellerIsAlsoRecipient_ShouldCreateRecipientAndPrimaryTransfers`
4. `ProcessSplitsForTransactionAsync_WhenPrimaryResidualAlreadyPaid_ShouldNotDuplicate`
5. `ProcessSplitsForTransactionAsync_WhenRecipientAlreadyPaid_ShouldNotDuplicate`
6. `ProcessSplitsForTransactionAsync_WhenLedgerFails_ShouldMarkTransferFailedAndAllowRetry`

### TransactionService

1. `CreateAsync_ShouldThrow_WhenStripeCardSplitsWithDirectChargeMode`
2. `CreateAsync_ShouldAllow_WhenOpenPixPixSplitsAndStripeDirectChargeMode`
3. `CreateAsync_ShouldAllow_WhenStripeCardSplitsWithDestinationChargeMode`
4. `CreateAsync_ShouldPersistFeeAllocationPolicy`

### WebhooksService refund

1. `PartialRefund_Twice_ShouldDebitOnlyRemainingAmounts`
2. `FullRefund_AfterPartialRefund_ShouldReverseOnlyRemainingAmounts`
3. `Refund_WithPrimaryResidualTransfer_ShouldReversePrimaryThroughSplitTransferFlow`
4. `Refund_WhenReverseTransitionFails_ShouldCreatePersistentReconciliationIssue`

### ReconciliationService

1. `Reconciliation_ShouldNotFlagDuplicate_WhenSameSellerHasRecipientAndPrimaryShare`
2. `Reconciliation_ShouldFlagDuplicate_WhenSameSellerHasTwoRecipientShares`
3. `Reconciliation_ShouldFlagRefundedTransaction_WhenPartiallyReversedTransferHasRemainingAmount`
4. `Reconciliation_ShouldFlagStuckSplitTransfer_WhenProcessingTooOld`

## Comandos de verificacao

Rodar no final:

```bash
dotnet test tests/FellowCore.Domain.Tests/FellowCore.Domain.Tests.csproj
dotnet test tests/FellowCore.Application.Tests/FellowCore.Application.Tests.csproj
dotnet test tests/FellowCore.Integration.Tests/FellowCore.Integration.Tests.csproj
```

Tambem rodar filtros especificos:

```bash
dotnet test tests/FellowCore.Application.Tests/FellowCore.Application.Tests.csproj --filter "FullyQualifiedName~Split"
dotnet test tests/FellowCore.Application.Tests/FellowCore.Application.Tests.csproj --filter "FullyQualifiedName~Refund"
dotnet test tests/FellowCore.Application.Tests/FellowCore.Application.Tests.csproj --filter "FullyQualifiedName~Reconciliation"
```

## Criterio final para considerar 10/10

So considerar finalizado quando todos os itens abaixo forem verdadeiros:

- Uma transacao com split distribui exatamente `NetAmount`, nem mais nem menos.
- `SPLIT_CLEARING` volta a zero quando todos os splits terminam.
- O seller primario pode ser tambem recipient sem perder residual.
- Retry nunca duplica pagamento.
- Retry nunca pula pagamento que nao aconteceu.
- Stripe direct charge com split e bloqueado apenas para Stripe.
- Pix OpenPix com split continua funcionando.
- Refund parcial unico debita cada seller proporcionalmente.
- Refund parcial sequencial debita apenas saldos remanescentes.
- Refund total apos parcial zera todos os `RemainingAmount`.
- Reconciliacao nao gera falso positivo para primary residual.
- Reconciliacao detecta marker travado e split parcialmente revertido com saldo restante.
- Todos os novos testes falham no codigo anterior e passam depois do fix.

## Nota final

O sistema esta proximo, mas ainda nao e 10/10. Os testes verdes atuais nao cobrem os estados intermediarios perigosos: crash entre marker e ledger, primary seller tambem como recipient, Pix em tenant com Stripe direct charge, e refunds parciais sequenciais.

O foco da proxima rodada deve ser menos "passar todos os testes" e mais provar invariantes financeiras:

```text
sum(distribuicoes) == transaction.NetAmount
sum(reversoes acumuladas) <= sum(distribuicoes)
remainingAmount nunca negativo
SPLIT_CLEARING == 0 apos conclusao
cada movimento financeiro tem idempotency key unica
```

Enquanto esses invariantes nao estiverem garantidos por codigo e testes, o split marketplace deve ser tratado como 8/10, nao 10/10.
