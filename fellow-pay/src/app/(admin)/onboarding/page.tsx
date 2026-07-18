"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useAuth, type OnboardSellerInput } from "@/context/AuthContext";
import { Illustration } from "@/components/ui/Illustration";

/**
 * Onboarding pós-SSO. User logado via Google escolhe se quer entrar como
 * Afiliado (cadastro mínimo: nome + CPF) ou Produtor (cadastro mínimo:
 * nome + CPF/CNPJ + nome fantasia). Em ambos os modos cria-se um Seller
 * vinculado, e tokens novos são emitidos com sellerId no payload.
 *
 * Ambos os modos persistem o mesmo Seller no MVP — a diferença é só semântica
 * (UX guia o user pro próximo passo: afiliado vai pro marketplace, produtor
 * vai criar primeiro produto).
 */

type Mode = "AFFILIATE" | "PRODUCER";

export default function OnboardingPage() {
  const { user, completeOnboarding } = useAuth();
  const router = useRouter();

  // null = ainda escolhendo entre os 2 cards. Após escolher, expande o form.
  const [selectedMode, setSelectedMode] = useState<Mode | null>(null);

  // Pré-preenche legalName com nome do user vindo do Google.
  const [legalName, setLegalName] = useState(user?.name ?? "");
  const [document, setDocument] = useState("");
  const [tradeName, setTradeName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Formata CPF/CNPJ enquanto digita — só os dígitos, com máscara visual.
  function maskDocument(raw: string) {
    const digits = raw.replace(/\D/g, "").slice(0, 14);
    if (digits.length <= 11) {
      // CPF: 000.000.000-00
      return digits
        .replace(/(\d{3})(\d)/, "$1.$2")
        .replace(/(\d{3})\.(\d{3})(\d)/, "$1.$2.$3")
        .replace(/(\d{3})\.(\d{3})\.(\d{3})(\d)/, "$1.$2.$3-$4");
    }
    // CNPJ: 00.000.000/0000-00
    return digits
      .replace(/(\d{2})(\d)/, "$1.$2")
      .replace(/(\d{2})\.(\d{3})(\d)/, "$1.$2.$3")
      .replace(/(\d{2})\.(\d{3})\.(\d{3})(\d)/, "$1.$2.$3/$4")
      .replace(/(\d{2})\.(\d{3})\.(\d{3})\/(\d{4})(\d)/, "$1.$2.$3/$4-$5");
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!selectedMode) return;

    setError(null);
    setSubmitting(true);

    const input: OnboardSellerInput = {
      mode: selectedMode,
      legalName: legalName.trim(),
      document: document.replace(/\D/g, ""),
      tradeName: tradeName.trim() || undefined,
    };

    const result = await completeOnboarding(input);
    if (result.success) {
      // Redireciona com base no modo: afiliado vai direto pro marketplace
      // explorar oportunidades; produtor vai pro painel pra entender métricas
      // antes de criar primeiro produto.
      router.push(selectedMode === "AFFILIATE" ? "/affiliate-marketplace" : "/");
    } else {
      setError(result.error || "Erro ao concluir cadastro. Tente novamente.");
      setSubmitting(false);
    }
  }

  // Tela 1: escolha do modo (cards lado a lado)
  if (!selectedMode) {
    return (
      <div className="min-h-[70vh] flex flex-col items-center justify-center px-4">
        <div className="w-full max-w-2xl text-center mb-8">
          <Illustration name="welcome" size="lg" className="mx-auto mb-4" />
          <h1 className="text-2xl font-bold text-gray-900 dark:text-white">
            Bem-vindo à Fellow Pay, {user?.name?.split(" ")[0] ?? "amigo"}!
          </h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-2 max-w-md mx-auto">
            Para começar, escolha como você quer usar a plataforma. Você pode
            mudar ou expandir depois.
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 w-full max-w-2xl">
          <ModeCard
            title="Sou Afiliado"
            subtitle="Quero promover produtos de outros produtores e ganhar comissão por venda."
            features={[
              "Acesso ao catálogo de produtos",
              "Link com tracking pra cada produto",
              "Comissão automática por venda confirmada",
            ]}
            cta="Começar como afiliado"
            onClick={() => setSelectedMode("AFFILIATE")}
            iconColor="text-brand-500"
            icon={
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="w-10 h-10" aria-hidden="true">
                <circle cx="12" cy="8" r="4" />
                <path d="M5 22a7 7 0 0 1 14 0" />
                <path d="M16 4l2 2 4-4" />
              </svg>
            }
          />
          <ModeCard
            title="Sou Produtor"
            subtitle="Quero criar e vender meus próprios produtos digitais, físicos ou serviços."
            features={[
              "Cadastro ilimitado de produtos",
              "Checkout próprio (Pix, cartão, boleto)",
              "Pode abrir afiliação e ter time de vendas",
            ]}
            cta="Começar como produtor"
            onClick={() => setSelectedMode("PRODUCER")}
            iconColor="text-brand-600"
            icon={
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="w-10 h-10" aria-hidden="true">
                <rect x="3" y="7" width="18" height="13" rx="2" />
                <path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                <path d="M3 13h18" />
              </svg>
            }
          />
        </div>
      </div>
    );
  }

  // Tela 2: form mínimo (dados pessoais + documento)
  const isCpf = document.replace(/\D/g, "").length <= 11;
  const isProducer = selectedMode === "PRODUCER";

  return (
    <div className="min-h-[70vh] flex items-center justify-center px-4">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-md rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 sm:p-8"
      >
        <button
          type="button"
          onClick={() => setSelectedMode(null)}
          className="text-xs text-gray-500 dark:text-gray-400 hover:text-brand-600 dark:hover:text-brand-300 mb-4 inline-flex items-center gap-1"
        >
          ← Voltar
        </button>
        <h2 className="text-lg font-bold text-gray-900 dark:text-white">
          {isProducer ? "Cadastro de Produtor" : "Cadastro de Afiliado"}
        </h2>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 mb-5">
          {isProducer
            ? "Para receber pagamentos como produtor, precisamos do CPF ou CNPJ que vai emitir as notas."
            : "Para receber comissões como afiliado, precisamos do seu CPF (pessoa física)."}
        </p>

        <div className="space-y-4">
          <div>
            <label
              htmlFor="legalName"
              className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5"
            >
              {isProducer ? "Razão social ou nome completo" : "Nome completo"}
            </label>
            <input
              id="legalName"
              type="text"
              value={legalName}
              onChange={(e) => setLegalName(e.target.value)}
              required
              minLength={3}
              className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-950 px-3 text-sm"
              placeholder={isProducer ? "Sua Empresa LTDA" : "Seu nome completo"}
            />
          </div>

          <div>
            <label
              htmlFor="document"
              className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5"
            >
              {isCpf ? "CPF" : "CNPJ"}
            </label>
            <input
              id="document"
              type="text"
              value={document}
              onChange={(e) => setDocument(maskDocument(e.target.value))}
              required
              className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-950 px-3 text-sm tabular-nums"
              placeholder={isCpf ? "000.000.000-00" : "00.000.000/0000-00"}
            />
            <p className="text-[10px] text-gray-400 dark:text-gray-500 mt-1">
              {isProducer
                ? "Aceita CPF (pessoa física) ou CNPJ (empresa)."
                : "Apenas CPF — afiliados pessoa física no MVP."}
            </p>
          </div>

          {isProducer && (
            <div>
              <label
                htmlFor="tradeName"
                className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5"
              >
                Nome fantasia <span className="text-gray-400">(opcional)</span>
              </label>
              <input
                id="tradeName"
                type="text"
                value={tradeName}
                onChange={(e) => setTradeName(e.target.value)}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-950 px-3 text-sm"
                placeholder="Como o público te conhece"
              />
            </div>
          )}

          {error && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 ring-1 ring-error-200/60 dark:ring-error-500/20 px-3 py-2 text-xs text-error-700 dark:text-error-300">
              {error}
            </div>
          )}

          <button
            type="submit"
            disabled={submitting}
            className="w-full h-11 rounded-xl bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white shadow-sm shadow-brand-500/20 transition-all disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {submitting ? "Criando..." : "Concluir cadastro"}
          </button>

          <p className="text-[10px] text-gray-400 dark:text-gray-500 text-center pt-2">
            Os dados podem ser editados depois nas configurações. Dados
            bancários para receber pagamentos serão pedidos quando você for
            sacar a primeira vez.
          </p>
        </div>
      </form>
    </div>
  );
}

function ModeCard({
  title,
  subtitle,
  features,
  cta,
  onClick,
  icon,
  iconColor,
}: {
  title: string;
  subtitle: string;
  features: string[];
  cta: string;
  onClick: () => void;
  icon: React.ReactNode;
  iconColor: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="group text-left rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 transition-all hover:border-brand-300 dark:hover:border-brand-500/40 hover:shadow-[0_8px_30px_-12px_rgba(123,97,255,0.25)] hover:-translate-y-0.5"
    >
      <div
        className={`inline-flex items-center justify-center w-14 h-14 rounded-xl bg-brand-50 dark:bg-brand-500/15 mb-4 ${iconColor}`}
      >
        {icon}
      </div>
      <h3 className="text-base font-bold text-gray-900 dark:text-white">
        {title}
      </h3>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1.5 mb-4 leading-relaxed">
        {subtitle}
      </p>
      <ul className="space-y-1.5 mb-5">
        {features.map((f) => (
          <li
            key={f}
            className="flex items-start gap-1.5 text-[11px] text-gray-600 dark:text-gray-300"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.5"
              strokeLinecap="round"
              strokeLinejoin="round"
              className="text-brand-500 shrink-0 mt-0.5"
              aria-hidden="true"
            >
              <polyline points="20 6 9 17 4 12" />
            </svg>
            <span>{f}</span>
          </li>
        ))}
      </ul>
      <span className="inline-flex items-center gap-1.5 text-xs font-semibold text-brand-600 dark:text-brand-300 group-hover:text-brand-700 dark:group-hover:text-brand-200">
        {cta}
        <svg
          width="12"
          height="12"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="transition-transform group-hover:translate-x-0.5"
          aria-hidden="true"
        >
          <line x1="5" y1="12" x2="19" y2="12" />
          <polyline points="12 5 19 12 12 19" />
        </svg>
      </span>
    </button>
  );
}
