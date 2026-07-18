# FellowCore - Relatorio para Claude Code chegar a 10/10

Data: 2026-05-04

Objetivo: este documento lista todos os pontos levantados na avaliacao do sistema e transforma cada gap em tarefas objetivas para o Claude Code levar o FellowCore a 10/10 em prontidao tecnica, observabilidade, lucro liquido, custo de providers e split marketplace.

Notas atuais estimadas:

```text
Produto/arquitetura geral: 8/10
Engenharia backend: 8/10
Prontidao de release: 6/10
Grafana/Prometheus: 6.5/10
Separacao de lucro/margem: 7/10
Custo dos providers: 7/10
Ledger de lucro liquido: 6/10
Split marketplace avancado: 5.5/10
```

Nota alvo:

```text
Todos os itens: 10/10
```

Regra para o Claude Code: nao considerar nenhum item finalizado sem evidencia objetiva em codigo, teste, migracao, dashboard, alerta, runbook ou resultado de comando.

---

## 1. Release/build - sair de 6/10 para 10/10

### Diagnostico atual

O projeto tem estrutura avancada, mas a branch atual nao esta pronta para release porque `dotnet test` falha no restore.

Falhas encontradas:

- `Kralizek.Extensions.Configuration.AWSSecretsManager >= 2.0.1` nao existe no NuGet publico.
- Conflito entre `AWSSDK.SecretsManager` 3.x e `AWSSDK.S3` 4.x por causa de `AWSSDK.Core`.
- Muitos arquivos modificados/novos/deletados deixam a branch dificil de validar.

### Tarefas para 10/10

- Corrigir dependencias AWS.
- Remover/substituir `Kralizek.Extensions.Configuration.AWSSecretsManager` por pacote valido ou adapter proprio.
- Alinhar AWS SDK em uma unica major version.
- Garantir que local, CI e container usem os mesmos pacotes.
- Rodar e corrigir:

```bash
dotnet restore
dotnet build
dotnet test
```

- Separar mudancas em commits logicos.
- Nao deixar arquivos gerados ou lixo de build versionados.

### Criterio de aceite

- `dotnet restore` passa.
- `dotnet build` passa.
- `dotnet test` passa.
- CI passa.
- Worktree fica entendivel e revisavel.

---

## 2. Grafana/Prometheus - sair de 6.5/10 para 10/10

### Diagnostico atual

Ja existe:

- `/metrics` via OpenTelemetry/Prometheus exporter.
- `infra/prometheus/prometheus.yml`.
- Dashboards Grafana versionados.
- Provisioning de datasource e dashboards.
- Regras de alerta Prometheus.

Gaps:

- Prometheus e Grafana nao estao no `docker-compose.yml`.
- Muitos alertas usam metricas que a aplicacao ainda nao emite.
- Falta canal real de alerta.
- Algumas expressoes precisam validacao contra nomes reais das metricas exportadas.

### Tarefas para 10/10

#### 2.1 Adicionar Prometheus e Grafana no compose

- Adicionar servico `prometheus`.
- Montar `infra/prometheus/prometheus.yml`.
- Montar `infra/alerts/fellowcore-alerts.yml`.
- Adicionar servico `grafana`.
- Montar provisioning:
  - `infra/grafana/provisioning/datasources`
  - `infra/grafana/provisioning/dashboards`
  - `infra/grafana/dashboards`
- Criar volume persistente do Grafana.
- Expor:
  - Prometheus: `9090`
  - Grafana: `3000`

#### 2.2 Implementar metricas faltantes

Metricas que devem existir em `/metrics`:

```text
fellowcore_circuit_breaker_state
fellowcore_provider_errors_total
fellowcore_provider_requests_total
fellowcore_webhook_delivery_failures_total
fellowcore_outbox_dead_letter_count
fellowcore_reconciliation_issues_total
fellowcore_ledger_global_imbalance_cents
fellowcore_payouts_pending_age_hours
fellowcore_payouts_failed_total
fellowcore_disputes_open_count
fellowcore_hangfire_last_heartbeat_seconds
fellowcore_hangfire_failed_count
fellowcore_health_check_status
```

Regras:

- Counters devem ser registrados no evento real.
- Gauges devem ser observable gauges consultando repositorios/servicos.
- Labels devem ser de baixa cardinalidade.
- Nao usar `transaction_id`, `seller_id`, `tenant_id` em metricas de alta frequencia.

#### 2.3 Validar alertas

- Validar nomes reais de metricas HTTP exportadas pelo OpenTelemetry.
- Corrigir alerta de CPU para usar `rate(process_cpu_seconds_total[5m])`.
- Rodar:

```bash
promtool check rules infra/alerts/fellowcore-alerts.yml
```

#### 2.4 Canal real de alerta

- Configurar Alertmanager ou Grafana Alerting.
- Canal permitido: Slack, Discord, PagerDuty, OpsGenie ou email operacional.
- Nao versionar secrets.
- Documentar responsavel/on-call.
- Disparar alerta de teste.

### Criterio de aceite

- `docker compose up` sobe Prometheus e Grafana.
- `http://localhost:9090/targets` mostra API UP.
- Grafana provisiona datasource e dashboards automaticamente.
- Todos os alertas usam metricas existentes.
- Alerta de teste chega no canal real.

---

## 3. Lucro/margem - sair de 7/10 para 10/10

### Diagnostico atual

O sistema ja possui:

```text
PlatformFeeAmount
ProviderCostAmount
PlatformMarginAmount = PlatformFeeAmount - ProviderCostAmount
```

Isso permite calcular lucro estimado por transacao.

Gap principal:

- A margem ainda parece mais relatorio/campo calculado do que contabilidade completa no ledger.

### Tarefas para 10/10

#### 3.1 Separar receita, custo e margem no ledger

Criar/usar contas de plataforma:

```text
PLATFORM_FEE
PROVIDER_COST
PLATFORM_MARGIN
PLATFORM_RECEIVABLE
PLATFORM_PAYOUT
```

Ao capturar uma transacao, o ledger deve demonstrar:

```text
Venda: 100.00
Taxa FellowCore: 1.50
Custo provider: 0.80
Margem FellowCore: 0.70
Seller net: 98.50
```

O resultado contabil deve permitir provar:

- Seller recebeu 98.50.
- FellowCore reconheceu receita bruta 1.50.
- Provider custou 0.80.
- FellowCore reteve margem 0.70.

#### 3.2 Criar servico de contabilizacao de margem

- Criar um servico dedicado, por exemplo `IMarginLedgerService`.
- Nao espalhar lancamentos financeiros em controllers ou workers.
- O servico deve receber transacao, rail, platform fee e provider cost.
- Deve gerar lancamentos double-entry balanceados.

#### 3.3 Dashboard financeiro de margem

Expor no dashboard:

- GMV/TPV.
- Seller net.
- Platform fees.
- Provider costs.
- Gross margin.
- Margin percent.
- Margin por provider.
- Margin por metodo.

### Criterio de aceite

- Ledger fecha com taxa, custo e margem.
- Testes cobrem PIX, credito, debito e boleto.
- Dashboard soma margem a partir de dados reais.

---

## 4. Custo dos providers - sair de 7/10 para 10/10

### Diagnostico atual

O sistema ja tem:

- `ProviderCostSchedule`.
- `ProviderCostService`.
- Seed de custos OpenPix/Stripe.
- Campo `ProviderCostAmount` na transacao.

Gaps:

- Custo ausente volta `0`, o que pode inflar margem.
- Custo atual e estimado nao estao claramente separados.
- Settlement real ainda precisa ajustar custo real.

### Tarefas para 10/10

#### 4.1 Impedir custo zero silencioso em producao

- Em Production, se nao houver `ProviderCostSchedule` ativo para provider/metodo, falhar ou gerar alerta P1/P0.
- Em Development/Testing, permitir fallback apenas com log explicito.

#### 4.2 Separar custo estimado e custo real

Adicionar modelo para:

```text
ProviderCostEstimatedAmount
ProviderCostActualAmount
ProviderCostAdjustmentAmount
```

Ou equivalente via entidades/lancamentos de settlement.

#### 4.3 Reconciliar custo real via settlement

- Stripe settlement deve atualizar custo real.
- OpenPix settlement/statement deve atualizar custo real.
- Divergencia acima da tolerancia deve criar `ReconciliationIssue`.

Tipos sugeridos:

```text
PROVIDER_COST_MISMATCH
PROVIDER_COST_MISSING
PROVIDER_COST_ACTUAL_GREATER_THAN_ESTIMATED
```

### Criterio de aceite

- Em producao nao existe margem calculada com custo provider zero sem alerta.
- Custo real reconciliado aparece no relatorio financeiro.
- Divergencias geram issue.

---

## 5. Ledger financeiro - sair de 6/10 para 10/10

### Diagnostico atual

O ledger ja possui double-entry e varias contas operacionais.

Gaps:

- Lucro liquido ainda nao esta totalmente contabilizado como fluxo separado.
- Provider cost e margem precisam virar lancamentos formais.
- Refund/chargeback precisam ajustar margem e custo proporcionalmente.

### Tarefas para 10/10

- Garantir que toda transacao capturada gere ledger balanceado.
- Garantir que taxa da plataforma, custo provider e margem sejam rastreaveis por transacao.
- Implementar reversao proporcional em refund parcial.
- Implementar reversao total em refund total.
- Implementar ajuste de chargeback:
  - seller net
  - platform fee
  - provider cost
  - dispute fee
  - margin impact
- Criar reconciliacao de ledger global e por transacao.

### Criterio de aceite

- Soma global do ledger fecha.
- Soma por transacao fecha.
- Refund parcial nao deixa taxa/margem incorretas.
- Chargeback ganho/perdido ajusta contas corretamente.

---

## 6. Split marketplace - sair de 5.5/10 para 10/10

### Diagnostico atual

Ja existe:

- Split por valor fixo.
- Split por percentual.
- `SplitRule`.
- Multiplos recebedores.
- Validacao de percentual ate 100%.
- `SplitProcessor`.
- `SplitTransfer` modelado.

Gaps:

- O split atual parece criar payouts diretamente.
- Recebedores nao sao necessariamente creditados no ledger antes do payout.
- `SplitTransfer` nao parece ser a fonte principal do fluxo.
- `FeeAllocationPolicy` existe, mas nao parece aplicado de verdade.
- Refund/chargeback proporcional por recebedor ainda falta.

### Tarefas para 10/10

#### 6.1 Criar split financeiro real antes do payout

Fluxo alvo:

```text
1. Transacao capturada
2. Calcular split sobre base definida
3. Criar SplitTransfer para cada recebedor
4. Debitar origem apropriada
5. Creditar ledger de cada recebedor
6. Marcar SplitTransfer como RESERVED/PAID_TO_BALANCE
7. Payout externo vira etapa separada
```

Regra: payout nunca deve ser a primeira representacao financeira do split.

#### 6.2 Usar `SplitTransfer` como fonte de verdade

- Criar transferencias no momento da captura.
- Status devem controlar o fluxo:

```text
PENDING
RESERVED
PROCESSING
PAID
FAILED
REVERSED
```

- Garantir idempotencia por transacao/recebedor.

#### 6.3 Implementar `FeeAllocationPolicy`

Politicas:

```text
PRIMARY_SELLER_PAYS_FEES
PROPORTIONAL_TO_RECIPIENTS
PLATFORM_ABSORBS
```

Comportamento esperado:

- `PRIMARY_SELLER_PAYS_FEES`: seller principal paga taxas.
- `PROPORTIONAL_TO_RECIPIENTS`: taxas rateadas entre recebedores.
- `PLATFORM_ABSORBS`: plataforma absorve custo e reduz margem.

#### 6.4 Criar `SplitCalculationService`

Entrada:

```text
gross amount
net amount
platform fee
provider cost
recipients
fee allocation policy
rounding policy
```

Saida:

```text
recipient gross share
recipient fee share
recipient provider cost share
recipient net share
rounding adjustment
```

#### 6.5 Definir regra formal para fixo + percentual

Escolher e documentar:

- fixos primeiro por prioridade; percentuais sobre restante
- ou percentuais sempre sobre net original

Depois implementar e testar.

#### 6.6 Refund e chargeback por recebedor

Implementar:

- refund parcial proporcional por recebedor
- refund total revertendo todos os splits
- chargeback congelando/debitando recebedores
- disputa ganha/perdida ajustando saldos

#### 6.7 Concorrencia

- Dois workers nao podem processar a mesma transacao duas vezes.
- Criar indice unico ou chave idempotente:

```text
TenantId + TransactionId + RecipientSellerId
```

ou equivalente.

### Criterio de aceite

- Recebedores sao creditados no ledger antes do payout.
- `SplitTransfer` controla o estado.
- Todas as `FeeAllocationPolicy` funcionam.
- Refund/chargeback revertem splits corretamente.
- Teste concorrente nao duplica credito.
- Reconciliacao detecta split divergente.

---

## 7. Reconciliacao - chegar a 10/10

### Tarefas

Adicionar validacoes para:

```text
PROVIDER_COST_MISMATCH
PROVIDER_COST_MISSING
MARGIN_MISMATCH
SPLIT_TOTAL_MISMATCH
SPLIT_LEDGER_MISSING
SPLIT_DUPLICATE_CREDIT
SPLIT_REFUND_NOT_REVERSED
PAYOUT_EXCEEDS_RECIPIENT_BALANCE
```

Reconciliation deve validar:

- ledger global
- ledger por transacao
- settlement externo
- provider cost estimado vs real
- margem estimada vs real
- split esperado vs ledger creditado
- payout externo vs saldo do recebedor

### Criterio de aceite

- Cada divergencia relevante vira `ReconciliationIssue`.
- Issues criticas geram metrica Prometheus e alerta.
- Dashboard financeiro mostra issues abertas.

---

## 8. Testes obrigatorios para 10/10

### Build/release

- `dotnet restore`
- `dotnet build`
- `dotnet test`
- CI completo

### Observabilidade

- `/metrics` expoe todas as metricas usadas nos alertas.
- `promtool check rules` passa.
- Prometheus sobe e target API fica UP.
- Grafana provisiona dashboards.
- Alerta de teste chega no canal real.

### Lucro/custo provider

- PIX OpenPix calcula platform fee, provider cost e margin.
- Stripe credito calcula platform fee, provider cost e margin.
- Stripe debito calcula platform fee, provider cost e margin.
- Stripe boleto calcula platform fee, provider cost e margin.
- Custo ausente em Production falha ou alerta.
- Settlement real ajusta custo real.

### Ledger

- Captura gera lancamentos balanceados.
- Refund parcial ajusta seller, fee, provider cost e margin.
- Refund total zera corretamente.
- Chargeback ganho/perdido ajusta contas.
- Ledger global fecha.

### Split marketplace

- Split percentual com 2+ recebedores.
- Split fixo com 2+ recebedores.
- Split fixo + percentual com prioridade.
- `PRIMARY_SELLER_PAYS_FEES`.
- `PROPORTIONAL_TO_RECIPIENTS`.
- `PLATFORM_ABSORBS`.
- Refund parcial por recebedor.
- Refund total por recebedor.
- Chargeback por recebedor.
- Worker concorrente nao duplica split.
- Reconciliation detecta divergencia.

---

## 9. Comandos finais que o Claude Code deve rodar

```bash
dotnet restore
dotnet build
dotnet test
docker compose config
docker compose up -d
curl -i --max-time 8 http://localhost:5195/health
curl -i --max-time 8 http://localhost:5195/metrics
curl -i --max-time 8 http://localhost:9090/-/ready
curl -i --max-time 8 http://localhost:3000/api/health
promtool check rules infra/alerts/fellowcore-alerts.yml
```

Se a porta da API for outra no ambiente local, ajustar os curls e documentar.

---

## 10. Definicao final de 10/10

O FellowCore so deve ser considerado 10/10 nos pontos levantados quando:

- Build, testes e CI passam.
- Prometheus e Grafana sobem no compose.
- Todos os alertas usam metricas reais.
- Canal real de alerta foi testado.
- O sistema calcula lucro por transacao.
- O custo dos providers e descontado e reconciliado.
- O ledger separa receita, custo e margem.
- O split credita cada recebedor no ledger antes de payout.
- `SplitTransfer` e fonte de verdade.
- `FeeAllocationPolicy` funciona de verdade.
- Refund e chargeback revertem split proporcionalmente.
- Reconciliation cobre provider cost, margem e split.
- Dashboards mostram dados reais e confiaveis.

