"use client";

import React from "react";
import type { SellerProfile } from "@/types";
import { paymentProviderLabel } from "@/lib/formatters/enums";

interface Props {
  profile: SellerProfile;
}

function formatDocument(doc: string): string {
  if (doc.length === 14) return doc.replace(/^(\d{2})(\d{3})(\d{3})(\d{4})(\d{2})$/, "$1.$2.$3/$4-$5");
  if (doc.length === 11) return doc.replace(/^(\d{3})(\d{3})(\d{3})(\d{2})$/, "$1.$2.$3-$4");
  return doc;
}

function formatDateTime(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  return d.toLocaleString("pt-BR", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function LockIcon() {
  return (
    <svg
      width="13"
      height="13"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <rect width="18" height="11" x="3" y="11" rx="2" ry="2" />
      <path d="M7 11V7a5 5 0 0 1 10 0v4" />
    </svg>
  );
}

function Field({ label, value, mono = false }: { label: string; value: React.ReactNode; mono?: boolean }) {
  return (
    <div className="flex flex-col gap-1.5 py-3 first:pt-0 last:pb-0 border-b border-gray-100 dark:border-gray-800/80 last:border-b-0">
      <span className="text-[11px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
        {label}
      </span>
      <span
        className={`text-sm font-medium text-gray-900 dark:text-white tabular-nums ${
          mono ? "font-mono text-xs text-gray-700 dark:text-gray-300" : ""
        }`}
      >
        {value}
      </span>
    </div>
  );
}

/**
 * Dados de identificação do seller. São derivados do KYC e não editáveis pelo
 * portal — alterações exigem fluxo administrativo (compliance/onboarding).
 */
export default function SellerIdentityCard({ profile }: Props) {
  return (
    <div className="rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 h-full">
      <div className="flex items-center justify-between p-5 lg:p-6 border-b border-gray-200/80 dark:border-gray-800">
        <div>
          <h3 className="text-base font-semibold text-gray-900 dark:text-white">Identificação</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Dados verificados pelo onboarding
          </p>
        </div>
        <span
          className="inline-flex items-center gap-1.5 rounded-full bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400 px-2.5 py-1 text-[11px] font-medium"
          title="Estes campos não podem ser alterados aqui. Contate o suporte para mudanças cadastrais."
        >
          <LockIcon />
          Somente leitura
        </span>
      </div>
      <div className="p-5 lg:p-6 divide-y divide-gray-100 dark:divide-gray-800/80">
        <Field label="Razão social" value={profile.legalName} />
        <Field label="Nome fantasia" value={profile.tradeName?.trim() || "—"} />
        <Field label="CNPJ / CPF" value={formatDocument(profile.document)} />
        <Field label="Provider preferido" value={paymentProviderLabel(profile.preferredProvider) ?? "—"} />
        <Field label="Conta externa" value={profile.externalAccountId || "—"} mono />
        <Field
          label="Cadastrado em"
          value={
            <>
              {formatDateTime(profile.createdAt)}
              {profile.updatedAt && profile.updatedAt !== profile.createdAt && (
                <span className="ml-2 text-xs font-normal text-gray-500 dark:text-gray-400">
                  · atualizado em {formatDateTime(profile.updatedAt)}
                </span>
              )}
            </>
          }
        />
      </div>
    </div>
  );
}
