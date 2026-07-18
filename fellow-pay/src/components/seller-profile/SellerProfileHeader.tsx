"use client";

import React from "react";
import type { SellerProfile } from "@/types";
import { sellerStatusLabel, sellerStatusKey, paymentProviderLabel } from "@/lib/formatters/enums";

interface Props {
  profile: SellerProfile;
}

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

function statusBadgeClasses(status: SellerProfile["status"]): string {
  const key = sellerStatusKey(status);
  if (key === "ACTIVE")
    return "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400 ring-success-500/20";
  if (key === "SUSPENDED" || key === "BLOCKED")
    return "bg-error-50 text-error-700 dark:bg-error-500/15 dark:text-error-400 ring-error-500/20";
  return "bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400 ring-warning-500/20";
}

/** Pequeno chip de metadata (since, provider, etc.) — visual leve. */
function MetaChip({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-white/70 dark:bg-white/[0.04] backdrop-blur ring-1 ring-inset ring-gray-200/80 dark:ring-gray-800 px-3 py-1">
      <span className="text-[10px] font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
        {label}
      </span>
      <span className="text-xs font-medium text-gray-800 dark:text-gray-200 tabular-nums">{value}</span>
    </span>
  );
}

export default function SellerProfileHeader({ profile }: Props) {
  const display = profile.tradeName?.trim() || profile.legalName;
  const status = sellerStatusLabel(profile.status);
  const provider = paymentProviderLabel(profile.preferredProvider);

  return (
    <div className="relative overflow-hidden rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900">
      {/* Background decorativo: gradient sutil em direção ao roxo da marca, só na metade superior. */}
      <div
        aria-hidden="true"
        className="absolute inset-x-0 top-0 h-32 bg-gradient-to-br from-brand-50 via-white to-white dark:from-brand-500/10 dark:via-gray-900 dark:to-gray-900"
      />
      <div
        aria-hidden="true"
        className="absolute -top-12 -right-12 w-48 h-48 rounded-full bg-brand-500/10 dark:bg-brand-500/15 blur-3xl"
      />

      <div className="relative p-6 lg:p-8">
        <div className="flex flex-col gap-6 lg:flex-row lg:items-start lg:justify-between">
          <div className="flex items-start gap-5">
            <div className="flex items-center justify-center w-20 h-20 rounded-2xl bg-gradient-to-br from-brand-500 to-brand-700 text-white text-2xl font-semibold shadow-lg shadow-brand-500/20 dark:shadow-brand-500/30 shrink-0">
              {initials(display)}
            </div>
            <div className="min-w-0">
              <h2 className="text-2xl font-semibold tracking-tight text-gray-900 dark:text-white truncate">
                {display}
              </h2>
              {profile.tradeName?.trim() && profile.tradeName.trim() !== profile.legalName && (
                <p className="text-sm text-gray-500 dark:text-gray-400 mt-0.5 truncate">
                  {profile.legalName}
                </p>
              )}
              <div className="flex flex-wrap items-center gap-2 mt-3">
                <span
                  className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ring-1 ring-inset ${statusBadgeClasses(
                    profile.status
                  )}`}
                >
                  <span className="w-1.5 h-1.5 rounded-full bg-current" />
                  {status}
                </span>
                <MetaChip label="CNPJ" value={formatDocument(profile.document)} />
                <MetaChip label="Na Fellow" value={relativeTime(profile.createdAt)} />
                {provider && <MetaChip label="Provider" value={provider} />}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
