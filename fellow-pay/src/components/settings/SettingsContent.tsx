"use client";
import React, { useEffect, useState } from "react";
import { sellerService } from "@/services/seller.service";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { PageHeader } from "@/components/ui/PageHeader";
import Input from "@/components/form/input/InputField";
import type { SellerProfile } from "@/types";

function formatDocument(doc: string): string {
  if (doc.length === 14) {
    return doc.replace(/^(\d{2})(\d{3})(\d{3})(\d{4})(\d{2})$/, "$1.$2.$3/$4-$5");
  }
  if (doc.length === 11) {
    return doc.replace(/^(\d{3})(\d{3})(\d{3})(\d{2})$/, "$1.$2.$3-$4");
  }
  return doc;
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleDateString("pt-BR");
}

export function SettingsContent() {
  const [profile, setProfile] = useState<SellerProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    sellerService
      .getProfile()
      .then(setProfile)
      .catch((err) => setError(err.message || "Erro ao carregar perfil"))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div className="space-y-6">
        <div>
          <div className="h-6 w-36 bg-gray-200 dark:bg-gray-700 rounded animate-pulse" />
          <div className="h-4 w-72 bg-gray-200 dark:bg-gray-700 rounded animate-pulse mt-2" />
        </div>
        <div className="grid grid-cols-12 gap-6">
          {[...Array(4)].map((_, i) => (
            <div key={i} className="col-span-12 lg:col-span-6">
              <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 h-48 animate-pulse" />
            </div>
          ))}
        </div>
      </div>
    );
  }

  if (error || !profile) {
    return (
      <div className="space-y-6">
        <PageHeader title="Configurações" />
        <div className="rounded-xl border border-error-200 bg-error-50 dark:border-error-800 dark:bg-error-900/20 p-5">
          <p className="text-sm text-error-700 dark:text-error-400">
            {error || "Não foi possível carregar o perfil."}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Configurações"
        subtitle="Gerencie os dados da sua conta, dados bancários e preferências."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
          </svg>
        }
      />

      <div className="grid grid-cols-12 gap-6">
        <div className="col-span-12 lg:col-span-6">
          <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 space-y-4">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
              Dados da Conta
            </h3>
            <div className="space-y-3">
              <Input label="Razão Social" type="text" value={profile.legalName} disabled />
              <Input label="Nome Fantasia" type="text" value={profile.tradeName ?? ""} disabled />
              <Input label="CNPJ" type="text" value={formatDocument(profile.document)} disabled className="font-mono" />
              <Input label="Email" type="text" value={profile.email} disabled />
            </div>
          </div>
        </div>

        <div className="col-span-12 lg:col-span-6">
          <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 space-y-4">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
              Dados Bancários
            </h3>
            <div className="space-y-3">
              <Input label="Chave PIX" type="text" value={profile.pixKey || "-"} disabled className="font-mono" />
              <Input label="Telefone" type="text" value={profile.mobilePhone || "-"} disabled />
            </div>
            <p className="text-xs text-gray-400 dark:text-gray-500">
              Para alterar dados bancários, entre em contato com o suporte.
            </p>
          </div>
        </div>

        <div className="col-span-12 lg:col-span-6">
          <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 space-y-4">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
              Webhook URL
            </h3>
            <Input
              label="URL de notificações"
              type="text"
              placeholder="https://seusite.com/webhooks/fellow"
              disabled
              className="font-mono"
              hint="Configure endpoints detalhados na página de Webhooks."
            />
          </div>
        </div>

        <div className="col-span-12 lg:col-span-6">
          <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 space-y-4">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
              Status da Conta
            </h3>
            <div className="space-y-3">
              <div className="flex items-center justify-between py-2">
                <span className="text-sm text-gray-700 dark:text-gray-300">Status</span>
                <span className="inline-flex items-center gap-1.5 text-xs font-medium text-success-700 dark:text-success-400 bg-success-50 dark:bg-success-500/10 px-2 py-0.5 rounded-full">
                  <span className="w-1.5 h-1.5 rounded-full bg-success-500" />
                  {profile.status === "ACTIVE" ? "Ativa" : profile.status}
                </span>
              </div>
              <div className="flex items-center justify-between py-2">
                <span className="text-sm text-gray-700 dark:text-gray-300">Conta criada em</span>
                <span className="text-xs text-gray-500 dark:text-gray-400">{formatDate(profile.createdAt)}</span>
              </div>
              <div className="flex items-center justify-between py-2">
                <span className="text-sm text-gray-700 dark:text-gray-300">ID do Seller</span>
                <IdDisplay id={profile.id} copyable />
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
