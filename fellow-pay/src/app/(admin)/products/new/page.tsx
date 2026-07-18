"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { ApiError } from "@/lib/api/client";
import { ImageUpload } from "@/components/ui/ImageUpload";
import { Select } from "@/components/ui/Select";
import { BackLink } from "@/components/ui/BackLink";
import { PageHeader } from "@/components/ui/PageHeader";
import type { AffiliationModeCode, ProductTypeCode } from "@/types";

/**
 * Form de criação de produto. Campos agrupados em 3 seções (Identidade,
 * Comercial, Afiliação) pra reduzir cognitive load — antes era um único
 * card flat misturando tudo.
 *
 * Layout: 2 colunas desktop (form esquerda + preview aside direita). Aside
 * mostra como o produto vai aparecer pro afiliado/comprador conforme você
 * preenche — feedback imediato.
 *
 * Sticky bottom bar com Cancelar + Criar — pattern do /products/[id]
 * OverviewTab. Visível em qualquer scroll.
 *
 * Após criação, redireciona para /products/{id} para edição/publicação.
 * Produto sempre nasce em DRAFT — produtor clica "Publicar" depois.
 */
export default function NewProductPage() {
  const router = useRouter();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  // coverFile fica em memória do browser até o submit — só aí faz upload
  // pro storage. Evita arquivos órfãos quando o usuário cancela.
  const [coverFile, setCoverFile] = useState<File | null>(null);
  const [price, setPrice] = useState("");
  const [type, setType] = useState<ProductTypeCode>("DIGITAL");
  const [deliveryUrl, setDeliveryUrl] = useState("");
  const [commission, setCommission] = useState("30");
  const [mode, setMode] = useState<AffiliationModeCode>("REQUEST");
  const [category, setCategory] = useState("");
  const [error, setError] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: async () => {
      // Two-phase submit:
      //   1. Se há arquivo pendente, faz upload AGORA e pega a URL
      //   2. Cria o produto com a URL (ou sem, se nada foi escolhido)
      // Falha no upload bloqueia a criação — caller exibe erro. Falha na
      // criação NÃO desfaz o upload (arquivo fica no storage, mas é
      // tradeoff aceitável vs complexidade de saga/compensation).
      let coverImageUrl: string | undefined;
      if (coverFile) {
        coverImageUrl = await marketplaceService.uploadProductCover(coverFile);
      }
      return marketplaceService.createProduct({
        name: name.trim(),
        description: description.trim() || undefined,
        coverImageUrl,
        price: parseFloat(price.replace(",", ".")),
        type,
        deliveryUrl: deliveryUrl.trim() || undefined,
        defaultAffiliateCommissionPercent: parseFloat(commission.replace(",", ".")),
        affiliationMode: mode,
        category: category.trim() || undefined,
      });
    },
    onSuccess: (product) => router.push(`/products/${product.id}`),
    onError: (err) => {
      setError(err instanceof ApiError ? err.message : (err instanceof Error ? err.message : "Erro ao criar produto."));
    },
  });

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    const priceNum = parseFloat(price.replace(",", "."));
    const commissionNum = parseFloat(commission.replace(",", "."));
    if (!name.trim()) return setError("Nome é obrigatório.");
    if (!Number.isFinite(priceNum) || priceNum <= 0) return setError("Preço deve ser maior que zero.");
    if (!Number.isFinite(commissionNum) || commissionNum < 0 || commissionNum > 100)
      return setError("Comissão deve estar entre 0 e 100.");
    create.mutate();
  }

  // Dados derivados pro preview aside — recalculam a cada keystroke.
  const previewPrice = parseFloat(price.replace(",", "."));
  const previewCommission = parseFloat(commission.replace(",", "."));
  const previewEarning =
    Number.isFinite(previewPrice) && Number.isFinite(previewCommission) && previewPrice > 0
      ? (previewPrice * previewCommission) / 100
      : null;
  const previewCoverUrl = coverFile ? URL.createObjectURL(coverFile) : null;

  return (
    <div className="space-y-6 pb-24">
      <BackLink fallbackHref="/products" />

      <PageHeader
        title="Novo produto"
        subtitle="Crie um produto para publicar no marketplace e abrir para afiliados promoverem."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
            <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
            <line x1="12" y1="22.08" x2="12" y2="12" />
          </svg>
        }
      />

      <form
        onSubmit={handleSubmit}
        className="grid grid-cols-1 lg:grid-cols-[1fr_320px] gap-6"
      >
        {/* COLUNA PRINCIPAL — Form em seções */}
        <div className="space-y-5">
          <FormSection title="Identidade" subtitle="Como o produto aparece para o comprador">
            <Field label="Nome*" htmlFor="name">
              <input
                id="name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                maxLength={200}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm text-gray-900 dark:text-white focus:outline-none focus:ring-1 focus:ring-brand-500"
                placeholder="Ex: Curso Completo de SQL"
              />
            </Field>

            <Field label="Descrição" htmlFor="description">
              <textarea
                id="description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                maxLength={5000}
                rows={4}
                className="w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 py-2 text-sm text-gray-900 dark:text-white focus:outline-none focus:ring-1 focus:ring-brand-500"
                placeholder="O que o cliente vai receber?"
              />
            </Field>

            <Field label="Capa do produto" htmlFor="cover" hint="PNG, JPEG ou WEBP até 5 MB · proporção 16:9 recomendada. Opcional, mas aumenta conversão.">
              <ImageUpload
                existingUrl={null}
                pendingFile={coverFile}
                onPickFile={setCoverFile}
                onRemove={() => setCoverFile(null)}
                emptyLabel="Clique para enviar a capa"
                hint=""
              />
            </Field>

            <Field label="Categoria" htmlFor="category" hint="Usada para filtros no marketplace. Ex: curso, ebook, mentoria, saúde.">
              <input
                id="category"
                value={category}
                onChange={(e) => setCategory(e.target.value)}
                maxLength={50}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm text-gray-900 dark:text-white focus:outline-none focus:ring-1 focus:ring-brand-500"
                placeholder="curso"
              />
            </Field>
          </FormSection>

          <FormSection title="Comercial" subtitle="Preço e entrega">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <Field label="Preço (R$)*" htmlFor="price">
                <input
                  id="price"
                  type="number"
                  step="0.01"
                  min="0.01"
                  value={price}
                  onChange={(e) => setPrice(e.target.value)}
                  required
                  className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm text-gray-900 dark:text-white tabular-nums focus:outline-none focus:ring-1 focus:ring-brand-500"
                  placeholder="297.00"
                />
              </Field>

              <Field label="Tipo*" htmlFor="type">
                <Select
                  id="type"
                  ariaLabel="Tipo de produto"
                  value={type}
                  onChange={(v) => setType(v as ProductTypeCode)}
                  options={[
                    { value: "DIGITAL", label: "Digital (curso, ebook, acesso)" },
                    { value: "PHYSICAL", label: "Físico" },
                    { value: "SERVICE", label: "Serviço" },
                  ]}
                />
              </Field>
            </div>

            {type === "DIGITAL" && (
              <Field
                label="URL de entrega"
                htmlFor="delivery"
                hint="Área de membros, drive, link de download. O cliente recebe após o pagamento confirmado."
              >
                <input
                  id="delivery"
                  type="url"
                  value={deliveryUrl}
                  onChange={(e) => setDeliveryUrl(e.target.value)}
                  maxLength={1000}
                  className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm text-gray-900 dark:text-white focus:outline-none focus:ring-1 focus:ring-brand-500"
                  placeholder="https://..."
                />
              </Field>
            )}
          </FormSection>

          <FormSection title="Afiliação" subtitle="Comissão e regras para outros sellers promoverem">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <Field label="Comissão default (%)*" htmlFor="commission" hint="Quanto o afiliado ganha por venda atribuída.">
                <input
                  id="commission"
                  type="number"
                  step="0.1"
                  min="0"
                  max="100"
                  value={commission}
                  onChange={(e) => setCommission(e.target.value)}
                  required
                  className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm text-gray-900 dark:text-white tabular-nums focus:outline-none focus:ring-1 focus:ring-brand-500"
                  placeholder="30"
                />
              </Field>

              <Field label="Modo de afiliação*" htmlFor="mode" hint="Define quem pode promover seu produto.">
                <Select
                  id="mode"
                  ariaLabel="Modo de afiliação"
                  value={mode}
                  onChange={(v) => setMode(v as AffiliationModeCode)}
                  options={[
                    { value: "OPEN", label: "Aberta (auto-aprova)" },
                    { value: "REQUEST", label: "Sob pedido (aprovo manualmente)" },
                    { value: "CLOSED", label: "Fechada (sem afiliados)" },
                  ]}
                />
              </Field>
            </div>
          </FormSection>

          {error && (
            <div className="rounded-lg border border-error-200 bg-error-50 dark:border-error-500/30 dark:bg-error-500/10 px-4 py-3 text-sm text-error-700 dark:text-error-400">
              {error}
            </div>
          )}
        </div>

        {/* ASIDE — Preview de como o produto vai ficar pro comprador/afiliado.
            Sticky no desktop pra acompanhar o scroll do form. */}
        <aside className="lg:sticky lg:top-20 self-start">
          <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-white/[0.03] overflow-hidden">
            <div className="px-4 py-3 border-b border-gray-100 dark:border-gray-800">
              <p className="text-[10px] uppercase tracking-[0.06em] font-semibold text-gray-500 dark:text-gray-400">
                Preview · como aparece para o afiliado
              </p>
            </div>

            {/* Cover */}
            <div className="aspect-[16/9] bg-gray-100 dark:bg-gray-800 overflow-hidden">
              {previewCoverUrl ? (
                /* eslint-disable-next-line @next/next/no-img-element */
                <img src={previewCoverUrl} alt="" className="w-full h-full object-cover" />
              ) : (
                <div className="w-full h-full flex items-center justify-center text-gray-400 dark:text-gray-600">
                  <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                    <rect x="3" y="3" width="18" height="18" rx="2" />
                    <circle cx="9" cy="9" r="2" />
                    <path d="m21 15-5-5L5 21" />
                  </svg>
                </div>
              )}
            </div>

            {/* Info */}
            <div className="p-4 space-y-3">
              <p className="text-sm font-semibold text-gray-900 dark:text-white line-clamp-2">
                {name.trim() || "Nome do produto"}
              </p>
              {description.trim() && (
                <p className="text-xs text-gray-500 dark:text-gray-400 line-clamp-3">
                  {description.trim()}
                </p>
              )}

              <div className="pt-2 border-t border-gray-100 dark:border-gray-800 space-y-1.5">
                <div className="flex items-baseline justify-between">
                  <span className="text-[10px] uppercase tracking-wider font-semibold text-gray-500 dark:text-gray-400">
                    Preço
                  </span>
                  <span className="text-base font-semibold text-gray-900 dark:text-white tabular-nums">
                    {Number.isFinite(previewPrice) && previewPrice > 0
                      ? previewPrice.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })
                      : "—"}
                  </span>
                </div>
                <div className="flex items-baseline justify-between">
                  <span className="text-[10px] uppercase tracking-wider font-semibold text-gray-500 dark:text-gray-400">
                    Comissão
                  </span>
                  <span className="text-base font-semibold text-brand-600 dark:text-brand-400 tabular-nums">
                    {Number.isFinite(previewCommission)
                      ? `${previewCommission.toFixed(1)}%`
                      : "—"}
                  </span>
                </div>
                {previewEarning !== null && (
                  <div className="flex items-baseline justify-between">
                    <span className="text-[10px] uppercase tracking-wider font-semibold text-gray-500 dark:text-gray-400">
                      Afiliado ganha
                    </span>
                    <span className="text-base font-semibold text-success-600 dark:text-success-400 tabular-nums">
                      {previewEarning.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                    </span>
                  </div>
                )}
              </div>
            </div>
          </div>
        </aside>

        {/* Sticky bottom bar — Cancelar + Criar. Posiciona fixo no rodapé
            da viewport pra ação ficar sempre acessível independente do scroll. */}
        <div className="lg:col-span-2 fixed bottom-0 left-0 right-0 z-30 border-t border-gray-200 bg-white/95 backdrop-blur-sm dark:border-gray-800 dark:bg-gray-900/95 lg:left-[270px]">
          <div className="flex items-center justify-end gap-2 px-6 py-3">
            <button
              type="button"
              onClick={() => router.push("/products")}
              disabled={create.isPending}
              className="h-10 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
            >
              Cancelar
            </button>
            <button
              type="submit"
              disabled={create.isPending}
              className="h-10 rounded-lg bg-brand-500 hover:bg-brand-600 px-5 text-sm font-semibold text-white disabled:opacity-50 transition-colors"
            >
              {create.isPending ? "Criando..." : "Criar produto"}
            </button>
          </div>
        </div>
      </form>
    </div>
  );
}

/** Card de seção do form — header com title + subtitle, body com children. */
function FormSection({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle?: string;
  children: React.ReactNode;
}) {
  // SEM overflow-hidden no card — o Select usa dropdown absolute que seria
  // clipado, virando scroll interno em vez de overlay. Rounded corners não
  // bleed sem o overflow porque borders são inset.
  return (
    <section className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-white/[0.03]">
      <header className="px-5 pt-5 pb-3 border-b border-gray-100 dark:border-gray-800">
        <h2 className="text-sm font-semibold text-gray-900 dark:text-white">
          {title}
        </h2>
        {subtitle && (
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            {subtitle}
          </p>
        )}
      </header>
      <div className="p-5 space-y-4">{children}</div>
    </section>
  );
}

function Field({
  label,
  htmlFor,
  hint,
  children,
}: {
  label: string;
  htmlFor: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label
        htmlFor={htmlFor}
        className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5"
      >
        {label}
      </label>
      {children}
      {hint && <p className="mt-1 text-[11px] text-gray-500 dark:text-gray-400">{hint}</p>}
    </div>
  );
}
