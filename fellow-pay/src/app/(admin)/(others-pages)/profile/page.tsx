"use client";

import React from "react";
import { useSellerProfile } from "@/hooks/useSellerProfile";
import SellerIdentityCard from "@/components/seller-profile/SellerIdentityCard";
import SellerContactCard from "@/components/seller-profile/SellerContactCard";
import AdvanceSettlementCard from "@/components/seller-profile/AdvanceSettlementCard";
import { PageHeader, PageHeaderChip } from "@/components/ui/PageHeader";
import { sellerStatusLabel, sellerStatusKey, paymentProviderLabel } from "@/lib/formatters/enums";
import type { SellerProfile } from "@/types";

/**
 * Página de Perfil — após o refactor, o PageHeader purple contém a
 * identidade RICA do seller (avatar + nome + status + meta chips).
 * O SellerProfileHeader standalone foi removido — sua função foi
 * absorvida pelo PageHeader com avatar + children slots.
 */

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  if (parts.length === 0) return "?";
  const first = parts[0]?.[0] ?? "";
  const last = parts.length > 1 ? parts[parts.length - 1][0] : "";
  return (first + last).toUpperCase() || "?";
}

function formatDocument(doc: string): string {
  if (doc.length === 14) return doc.replace(/^(\d{2})(\d{3})(\d{3})(\d{4})(\d{2})$/, "$1.$2.$3/$4-$5");
  if (doc.length === 11) return doc.replace(/^(\d{3})(\d{3})(\d{3})(\d{2})$/, "$1.$2.$3-$4");
  return doc;
}

function relativeTime(iso: string): string {
  const created = new Date(iso);
  if (isNaN(created.getTime())) return "—";
  const diffMs = Date.now() - created.getTime();
  const days = Math.floor(diffMs / (1000 * 60 * 60 * 24));
  if (days < 1) return "hoje";
  if (days < 30) return `há ${days} dia${days === 1 ? "" : "s"}`;
  const months = Math.floor(days / 30);
  if (months < 12) return `há ${months} ${months === 1 ? "mês" : "meses"}`;
  const years = Math.floor(months / 12);
  return `há ${years} ano${years === 1 ? "" : "s"}`;
}

function statusTone(status: SellerProfile["status"]): "default" | "success" | "warning" | "error" {
  const key = sellerStatusKey(status);
  if (key === "ACTIVE") return "success";
  if (key === "SUSPENDED" || key === "BLOCKED") return "error";
  return "warning";
}

function ProfileHero({ profile }: { profile: SellerProfile }) {
  const display = profile.tradeName?.trim() || profile.legalName;
  const status = sellerStatusLabel(profile.status);
  const provider = paymentProviderLabel(profile.preferredProvider);
  const hasDifferentLegal =
    profile.tradeName?.trim() && profile.tradeName.trim() !== profile.legalName;

  return (
    <PageHeader
      size="hero"
      title={display}
      subtitle={hasDifferentLegal ? profile.legalName : undefined}
      avatar={
        // Avatar branco glass — invertido sobre o bg purple do header,
        // mesmo princípio do pill do user dropdown. shadow-inner dá depth.
        // w-20 h-20 acompanha o size="hero" (padding/texto maiores).
        <div className="inline-flex items-center justify-center w-20 h-20 rounded-2xl bg-white text-brand-600 text-2xl font-bold shadow-inner">
          {initials(display)}
        </div>
      }
    >
      <div className="flex flex-wrap gap-2">
        <PageHeaderChip
          tone={statusTone(profile.status)}
          value={
            <>
              <span className="w-1.5 h-1.5 rounded-full bg-current inline-block mr-1" />
              {status}
            </>
          }
        />
        <PageHeaderChip label="CNPJ" value={formatDocument(profile.document)} />
        <PageHeaderChip label="Na Fellow" value={relativeTime(profile.createdAt)} />
        {provider && <PageHeaderChip label="Provider" value={provider} />}
      </div>
    </PageHeader>
  );
}

function HeroSkeleton() {
  return (
    <div className="rounded-2xl bg-gradient-to-br from-brand-500/30 to-brand-600/30 p-6 lg:p-8 animate-pulse">
      <div className="flex items-start gap-4">
        <div className="w-16 h-16 rounded-2xl bg-white/30" />
        <div className="flex-1 space-y-3">
          <div className="h-6 w-64 bg-white/30 rounded" />
          <div className="h-3 w-40 bg-white/20 rounded" />
          <div className="flex gap-2 pt-1">
            <div className="h-6 w-20 bg-white/20 rounded-full" />
            <div className="h-6 w-32 bg-white/20 rounded-full" />
            <div className="h-6 w-28 bg-white/20 rounded-full" />
          </div>
        </div>
      </div>
    </div>
  );
}

function CardSkeleton() {
  return (
    <div className="rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 animate-pulse">
      <div className="p-5 lg:p-6 border-b border-gray-200/80 dark:border-gray-800">
        <div className="h-4 w-32 bg-gray-200 dark:bg-gray-700 rounded mb-2" />
        <div className="h-3 w-48 bg-gray-100 dark:bg-gray-800 rounded" />
      </div>
      <div className="p-5 lg:p-6 space-y-4">
        {[...Array(4)].map((_, i) => (
          <div key={i} className="space-y-2">
            <div className="h-3 w-24 bg-gray-100 dark:bg-gray-800 rounded" />
            <div className="h-4 w-44 bg-gray-200 dark:bg-gray-700 rounded" />
          </div>
        ))}
      </div>
    </div>
  );
}

export default function ProfilePage() {
  const { profile, loading, error, update } = useSellerProfile();

  return (
    <div className="space-y-5">
      {loading && (
        <div className="space-y-5">
          <HeroSkeleton />
          <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
            <CardSkeleton />
            <CardSkeleton />
          </div>
        </div>
      )}

      {!loading && error && (
        <>
          <PageHeader
            title="Perfil"
            subtitle="Dados cadastrais do seller."
          />
          <div className="rounded-2xl border border-error-200 bg-error-50 dark:border-error-800/40 dark:bg-error-900/20 p-5">
            <p className="text-sm font-medium text-error-700 dark:text-error-400">{error}</p>
          </div>
        </>
      )}

      {!loading && !error && profile && (
        <>
          <ProfileHero profile={profile} />
          <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
            <SellerIdentityCard profile={profile} />
            <SellerContactCard profile={profile} onSave={update} />
          </div>
          <AdvanceSettlementCard profile={profile} onSave={update} />
        </>
      )}
    </div>
  );
}
