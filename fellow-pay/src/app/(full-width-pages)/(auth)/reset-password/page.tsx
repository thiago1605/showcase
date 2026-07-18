"use client";
import Image from "next/image";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import React, { useState, Suspense } from "react";
import { api } from "@/lib/api/client";
import Input from "@/components/form/input/InputField";

function ResetPasswordForm() {
  const searchParams = useSearchParams();
  const token = searchParams.get("token") || "";
  const emailParam = searchParams.get("email") || "";

  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");

    if (password.length < 8) {
      setError("A senha deve ter pelo menos 8 caracteres.");
      return;
    }

    if (password !== confirmPassword) {
      setError("As senhas não coincidem.");
      return;
    }

    setIsSubmitting(true);

    try {
      await api.post("/api/v1/auth/reset-password", {
        email: emailParam,
        token,
        newPassword: password,
      });
      setSuccess(true);
    } catch {
      setError("Link inválido ou expirado. Solicite um novo.");
    }

    setIsSubmitting(false);
  };

  if (success) {
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
          Senha redefinida
        </h1>
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-6">
          Sua senha foi alterada com sucesso. Faça login com a nova senha.
        </p>
        <Link
          href="/signin"
          className="inline-flex rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 transition-colors"
        >
          Ir para login
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
        Redefinir senha
      </h1>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-8">
        Escolha uma nova senha para sua conta.
      </p>

      {error && (
        <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
          {error}
        </div>
      )}

      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <Input
          label="Nova senha"
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          placeholder="Mínimo 8 caracteres"
          autoComplete="new-password"
          minLength={8}
          required
        />
        <Input
          label="Confirmar senha"
          type="password"
          value={confirmPassword}
          onChange={(e) => setConfirmPassword(e.target.value)}
          placeholder="Repita a nova senha"
          autoComplete="new-password"
          required
        />
        <button
          type="submit"
          disabled={isSubmitting}
          className="w-full h-12 rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.998] px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {isSubmitting ? "Redefinindo..." : "Redefinir senha"}
        </button>
      </form>
    </div>
  );
}

export default function ResetPasswordPage() {
  return (
    <Suspense fallback={<div className="w-full max-w-sm mx-auto"><p className="text-gray-500">Carregando...</p></div>}>
      <ResetPasswordForm />
    </Suspense>
  );
}
