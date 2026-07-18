"use client";
import Image from "next/image";
import Link from "next/link";
import React, { useState } from "react";
import { api } from "@/lib/api/client";
import Input from "@/components/form/input/InputField";

export default function ForgotPasswordPage() {
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState("");

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsSubmitting(true);

    try {
      await api.post("/api/v1/auth/forgot-password", { email });
      setSent(true);
    } catch {
      // Always show success to prevent email enumeration
      setSent(true);
    }

    setIsSubmitting(false);
  };

  if (sent) {
    return (
      <div className="w-full max-w-sm mx-auto">
        <div className="lg:hidden mb-8">
          <Image width={120} height={32} src="/images/fellow/fellow-pay-full-logo.PNG" alt="Fellow Pay" />
        </div>
        <div className="w-12 h-12 rounded-full bg-success-50 dark:bg-success-500/10 flex items-center justify-center mb-4">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" className="text-success-600 dark:text-success-400">
            <path d="M20 6L9 17l-5-5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </div>
        <h1 className="text-2xl font-semibold text-gray-900 dark:text-white mb-2">
          Email enviado
        </h1>
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-6">
          Se o email <strong>{email}</strong> estiver cadastrado, você receberá um link para redefinir sua senha.
        </p>
        <Link
          href="/signin"
          className="inline-flex text-sm text-brand-600 hover:text-brand-700 dark:text-brand-400"
        >
          Voltar para login
        </Link>
      </div>
    );
  }

  return (
    <div className="w-full max-w-sm mx-auto">
      <div className="lg:hidden mb-8">
        <Image width={120} height={32} src="/images/fellow/fellow-pay-full-logo.PNG" alt="Fellow Pay" />
      </div>
      <h1 className="text-2xl font-semibold text-gray-900 dark:text-white mb-2">
        Esqueceu a senha?
      </h1>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-8">
        Informe seu email e enviaremos um link para redefinir sua senha.
      </p>

      {error && (
        <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
          {error}
        </div>
      )}

      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <Input
          label="Email"
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="seu@email.com"
          autoComplete="email"
          required
          autoFocus
        />
        <button
          type="submit"
          disabled={isSubmitting}
          className="w-full h-12 rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.998] px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {isSubmitting ? "Enviando..." : "Enviar link"}
        </button>
      </form>
      <div className="mt-5">
        <Link href="/signin" className="text-sm text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-300">
          Voltar para login
        </Link>
      </div>
    </div>
  );
}
