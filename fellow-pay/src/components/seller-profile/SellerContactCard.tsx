"use client";

import React, { useState } from "react";
import type { SellerProfile, UpdateSellerProfileRequest } from "@/types";
import { useModal } from "@/hooks/useModal";
import { Modal } from "@/components/ui/modal";
import Button from "@/components/ui/button/Button";
import Input from "@/components/form/input/InputField";
import { ApiError } from "@/lib/api/client";

interface Props {
  profile: SellerProfile;
  onSave: (patch: UpdateSellerProfileRequest) => Promise<SellerProfile>;
}

function MailIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <rect width="20" height="16" x="2" y="4" rx="2" />
      <path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7" />
    </svg>
  );
}
function PhoneIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z" />
    </svg>
  );
}
function PixIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 2 2 12l10 10 10-10z" />
      <path d="m7 12 5-5 5 5-5 5z" />
    </svg>
  );
}
function StoreIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="m2 7 4.41-4.41A2 2 0 0 1 7.83 2h8.34a2 2 0 0 1 1.42.59L22 7" />
      <path d="M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8" />
      <path d="M15 22v-4a2 2 0 0 0-2-2h-2a2 2 0 0 0-2 2v4" />
      <path d="M2 7h20" />
      <path d="M22 7v3a2 2 0 0 1-2 2 2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 16 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 12 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 8 12a2.7 2.7 0 0 1-1.59-.63.7.7 0 0 0-.82 0A2.7 2.7 0 0 1 4 12a2 2 0 0 1-2-2V7" />
    </svg>
  );
}
function EditIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4Z" />
    </svg>
  );
}

interface FieldRowProps {
  icon: React.ReactNode;
  label: string;
  value: string | null;
  placeholder?: string;
}

function FieldRow({ icon, label, value, placeholder = "Não informado" }: FieldRowProps) {
  const filled = !!value && value.trim().length > 0;
  return (
    <div className="flex items-center gap-3 py-3 first:pt-0 last:pb-0 border-b border-gray-100 dark:border-gray-800/80 last:border-b-0">
      <span
        className={`flex items-center justify-center w-9 h-9 rounded-lg shrink-0 ${
          filled
            ? "bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-300"
            : "bg-gray-100 text-gray-400 dark:bg-gray-800 dark:text-gray-500"
        }`}
      >
        {icon}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-[11px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
          {label}
        </p>
        <p
          className={`text-sm font-medium tabular-nums truncate ${
            filled ? "text-gray-900 dark:text-white" : "text-gray-400 dark:text-gray-500 italic"
          }`}
        >
          {filled ? value : placeholder}
        </p>
      </div>
    </div>
  );
}

/** Campos editáveis pelo seller (correspondem ao UpdateSellerDto do backend). */
export default function SellerContactCard({ profile, onSave }: Props) {
  const { isOpen, openModal, closeModal } = useModal();
  const [tradeName, setTradeName] = useState(profile.tradeName ?? "");
  const [email, setEmail] = useState(profile.email);
  const [mobilePhone, setMobilePhone] = useState(profile.mobilePhone ?? "");
  const [pixKey, setPixKey] = useState(profile.pixKey ?? "");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleOpen = () => {
    setTradeName(profile.tradeName ?? "");
    setEmail(profile.email);
    setMobilePhone(profile.mobilePhone ?? "");
    setPixKey(profile.pixKey ?? "");
    setError(null);
    openModal();
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await onSave({
        tradeName: tradeName.trim() || null,
        email: email.trim(),
        mobilePhone: mobilePhone.trim() || null,
        pixKey: pixKey.trim() || null,
      });
      closeModal();
    } catch (err) {
      if (err instanceof ApiError) setError(err.message);
      else setError("Não foi possível salvar as alterações.");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      <div className="rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 h-full">
        <div className="flex items-center justify-between p-5 lg:p-6 border-b border-gray-200/80 dark:border-gray-800">
          <div>
            <h3 className="text-base font-semibold text-gray-900 dark:text-white">Contato e PIX</h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
              Como te encontramos e onde recebemos
            </p>
          </div>
          <button
            onClick={handleOpen}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.98] px-3.5 py-2 text-xs font-medium text-white transition-colors"
          >
            <EditIcon />
            Editar
          </button>
        </div>
        <div className="p-5 lg:p-6 divide-y divide-gray-100 dark:divide-gray-800/80">
          <FieldRow icon={<MailIcon />} label="E-mail" value={profile.email} />
          <FieldRow icon={<PhoneIcon />} label="Telefone" value={profile.mobilePhone} />
          <FieldRow icon={<PixIcon />} label="Chave PIX" value={profile.pixKey} />
          <FieldRow icon={<StoreIcon />} label="Nome fantasia" value={profile.tradeName} />
        </div>
      </div>

      <Modal isOpen={isOpen} onClose={closeModal} className="max-w-[600px] m-4">
        <div className="relative w-full max-w-[600px] rounded-3xl bg-white p-6 dark:bg-gray-900 lg:p-8">
          <h4 className="mb-1 text-xl font-semibold text-gray-900 dark:text-white">
            Editar contato e PIX
          </h4>
          <p className="mb-6 text-sm text-gray-500 dark:text-gray-400">
            Atualize seus dados de contato e a chave PIX usada para recebimentos.
          </p>
          <form onSubmit={handleSubmit} className="flex flex-col gap-4">
            <Input
              label="E-mail"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
            <Input
              label="Telefone"
              type="tel"
              value={mobilePhone}
              onChange={(e) => setMobilePhone(e.target.value)}
              placeholder="+55 11 99999-9999"
            />
            <Input
              label="Chave PIX"
              type="text"
              value={pixKey}
              onChange={(e) => setPixKey(e.target.value)}
              placeholder="CPF, CNPJ, e-mail, telefone ou chave aleatória"
            />
            <Input
              label="Nome fantasia"
              type="text"
              value={tradeName}
              onChange={(e) => setTradeName(e.target.value)}
              placeholder="Como sua marca aparece no checkout"
            />

            {error && (
              <div className="rounded-md bg-error-50 dark:bg-error-500/10 px-3 py-2 text-sm text-error-700 dark:text-error-400">
                {error}
              </div>
            )}

            <div className="flex items-center gap-3 mt-2 justify-end">
              <Button size="sm" variant="outline" type="button" onClick={closeModal} disabled={submitting}>
                Cancelar
              </Button>
              <Button size="sm" type="submit" disabled={submitting}>
                {submitting ? "Salvando..." : "Salvar"}
              </Button>
            </div>
          </form>
        </div>
      </Modal>
    </>
  );
}
