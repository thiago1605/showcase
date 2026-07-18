"use client";
import { useAuth } from "@/context/AuthContext";
import Image from "next/image";
import Link from "next/link";
import Script from "next/script";
import { useRouter } from "next/navigation";
import React, { useCallback, useEffect, useRef, useState } from "react";
import Input from "@/components/form/input/InputField";

/**
 * Tipos mínimos da Google Identity Services library. A lib é carregada via
 * <Script> abaixo e disponibiliza window.google.accounts.id no global. Só
 * declaramos o subset que usamos (initialize + prompt + callback).
 */
interface GoogleCredentialResponse {
  credential: string;
  select_by?: string;
}
interface GoogleAccountsId {
  initialize: (config: {
    client_id: string;
    callback: (response: GoogleCredentialResponse) => void;
    auto_select?: boolean;
    ux_mode?: "popup" | "redirect";
  }) => void;
  prompt: (
    momentListener?: (notification: {
      isNotDisplayed: () => boolean;
      isSkippedMoment: () => boolean;
      isDismissedMoment: () => boolean;
      getNotDisplayedReason: () => string;
      getSkippedReason: () => string;
      getDismissedReason: () => string;
    }) => void,
  ) => void;
  disableAutoSelect: () => void;
}
declare global {
  interface Window {
    google?: {
      accounts: {
        id: GoogleAccountsId;
      };
    };
  }
}

interface ToggleProps {
  checked: boolean;
  onChange: (next: boolean) => void;
  label: string;
}

const MobileLogo = () => (
  <div className="lg:hidden mb-6 flex justify-center">
    <Image
      width={120}
      height={32}
      src="/images/fellow/fellow-pay-full-logo-no-bg-light-mode.png"
      alt="Fellow Pay"
      className="dark:hidden"
      priority
    />
    <div className="hidden dark:block bg-white rounded-md px-2 py-1 w-fit">
      <Image
        width={120}
        height={32}
        src="/images/fellow/fellow-pay-full-logo-no-bg-light-mode.png"
        alt="Fellow Pay"
        priority
      />
    </div>
  </div>
);

const Pill = () => (
  <div className="flex justify-center mb-5">
    <div className="inline-flex items-center gap-2 rounded-full pill-gradient-brand px-4 py-2 text-white">
      <svg
        xmlns="http://www.w3.org/2000/svg"
        width="16"
        height="16"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z" />
      </svg>
      <span className="text-[14px] font-medium">Conecte. Processe. Escale.</span>
    </div>
  </div>
);

const Toggle = ({ checked, onChange, label }: ToggleProps) => (
  <label className="flex items-center gap-3 cursor-pointer select-none">
    <span className="text-[14px] font-light text-[#52525c] dark:text-gray-300">{label}</span>
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={
        "relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors " +
        (checked ? "bg-brand-500" : "bg-gray-300 dark:bg-gray-700")
      }
    >
      <span
        className={
          "inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform " +
          (checked ? "translate-x-[18px]" : "translate-x-0.5")
        }
      />
    </button>
  </label>
);

export default function SignInForm() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [remember, setRemember] = useState(false);
  const [error, setError] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [mfaRequired, setMfaRequired] = useState(false);
  const [mfaToken, setMfaToken] = useState("");
  const [totpCode, setTotpCode] = useState("");

  const { login, loginWithGoogle, verifyMfa } = useAuth();
  const router = useRouter();

  // Carrega o email salvo no localStorage (se o user marcou "Lembrar meus
  // dados" no login anterior). Importante: NUNCA salvamos senha — só email.
  // A senha continua sendo digitada todo login (boa prática de segurança).
  useEffect(() => {
    try {
      const savedEmail = localStorage.getItem("fellowpay:rememberedEmail");
      if (savedEmail) {
        setEmail(savedEmail);
        setRemember(true);
      }
    } catch {
      // localStorage pode estar indisponível (Safari privado, etc) — silenciamos.
    }
  }, []);

  // Google Identity Services — `initializedRef` é só uma guarda de idempotência
  // (não disparamos re-render quando o GSI carrega; verificamos `window.google`
  // em tempo real no click handler). Isso evita lint warning de set-state-in-effect.
  const [googleSubmitting, setGoogleSubmitting] = useState(false);
  const googleClientId = process.env.NEXT_PUBLIC_GOOGLE_CLIENT_ID;
  const initializedRef = useRef(false);

  const handleGoogleCredential = useCallback(
    async (response: GoogleCredentialResponse) => {
      setError("");
      setGoogleSubmitting(true);
      const result = await loginWithGoogle(response.credential);
      if (result.success) {
        router.push("/");
      } else {
        setError(result.error || "Não foi possível autenticar com o Google.");
      }
      setGoogleSubmitting(false);
    },
    [loginWithGoogle, router],
  );

  const initGoogle = useCallback(() => {
    if (initializedRef.current) return;
    if (!googleClientId || !window.google?.accounts?.id) return;
    window.google.accounts.id.initialize({
      client_id: googleClientId,
      callback: handleGoogleCredential,
      auto_select: false,
      ux_mode: "popup",
    });
    initializedRef.current = true;
  }, [googleClientId, handleGoogleCredential]);

  const handleGoogleClick = () => {
    if (!googleClientId) {
      setError(
        "Login com Google não está configurado neste ambiente. Use email e senha.",
      );
      return;
    }
    // Idempotente — se o script já carregou via onLoad, é no-op. Se chegou
    // antes deste handler (browser cache), inicializa aqui.
    initGoogle();
    if (!window.google?.accounts?.id) {
      setError(
        "Serviço do Google ainda carregando. Tente novamente em instantes.",
      );
      return;
    }
    window.google.accounts.id.prompt((notification) => {
      // O prompt pode ser suprimido pelo browser (cookie do FedCM bloqueado,
      // user fechou antes, etc). Comunicamos isso pra evitar UX silenciosa.
      if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
        setError(
          "Pop-up do Google bloqueado pelo navegador. Permita pop-ups para este site e tente novamente.",
        );
      }
    });
  };

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsSubmitting(true);

    const result = await login(email, password);

    if (result.success) {
      // Persiste / limpa o email lembrado conforme o toggle. Só email — a
      // senha nunca é armazenada (boa prática contra XSS / leak de localStorage).
      try {
        if (remember) {
          localStorage.setItem("fellowpay:rememberedEmail", email);
        } else {
          localStorage.removeItem("fellowpay:rememberedEmail");
        }
      } catch {
        // Storage indisponível — login mesmo assim funciona, só sem persistência.
      }
      router.push("/");
    } else if (result.requiresMfa) {
      setMfaRequired(true);
      setMfaToken(result.mfaToken || "");
    } else {
      setError(result.error || "Credenciais inválidas.");
    }

    setIsSubmitting(false);
  };

  const handleMfa = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsSubmitting(true);

    try {
      await verifyMfa(mfaToken, totpCode);
      router.push("/");
    } catch {
      setError("Código inválido. Tente novamente.");
    }

    setIsSubmitting(false);
  };

  // CTA preto com texto na cor da marca (roxo Fellow Pay) — mais vivo.
  const ctaClass =
    "w-full h-12 rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.998] " +
    "px-4 py-2 text-[14px] font-medium text-white shadow-sm " +
    "transition-colors disabled:opacity-50 disabled:cursor-not-allowed";

  if (mfaRequired) {
    return (
      <div className="w-full max-w-[448px] mx-auto">
        <MobileLogo />
        <Pill />
        <div className="mb-5 text-center">
          <h1 className="font-display text-[26px] font-semibold leading-[1.2] text-gray-900 dark:text-white tracking-tight">
            Verificação em 2 fatores
          </h1>
          <p className="text-sm font-light text-[#71717b] dark:text-gray-400 mt-2">
            Digite o código do seu app autenticador para continuar.
          </p>
        </div>

        {error && (
          <div className="mb-6 p-3 rounded-md bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
            {error}
          </div>
        )}

        <form onSubmit={handleMfa} className="flex flex-col gap-5">
          <Input
            label="Código TOTP"
            type="text"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            value={totpCode}
            onChange={(e) => setTotpCode(e.target.value.replace(/\D/g, ""))}
            className="text-center text-2xl font-mono tracking-widest"
            autoFocus
          />
          <button
            type="submit"
            disabled={isSubmitting || totpCode.length < 6}
            className={ctaClass}
          >
            {isSubmitting ? "Verificando..." : "Verificar"}
          </button>
        </form>
      </div>
    );
  }

  return (
    <div className="w-full max-w-[448px] mx-auto">
      <MobileLogo />
      <Pill />

      <div className="mb-5 text-center">
        <h1 className="font-display text-[24px] font-medium leading-[32px] tracking-[-0.6px] text-[#18181b] dark:text-white">
          Boas-vindas de volta à Fellow Pay
        </h1>
        <p className="text-sm font-light text-[#71717b] dark:text-gray-400 mt-2">
          Gerencie suas contas e acompanhe seus resultados em tempo real
        </p>
      </div>

      {error && (
        <div className="mb-6 p-3 rounded-md bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
          {error}
        </div>
      )}

      <form onSubmit={handleLogin} className="flex flex-col gap-5">
        <Input
          label="Email"
          type="email"
          placeholder="email@email.com"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          autoComplete="email"
          required
          autoFocus
        />

        <Input
          label="Senha"
          placeholder="••••••••••••"
          type={showPassword ? "text" : "password"}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="current-password"
          required
          rightSlot={
            <button
              type="button"
              onClick={() => setShowPassword(!showPassword)}
              aria-label={showPassword ? "Ocultar senha" : "Mostrar senha"}
              className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition-colors"
            >
              {showPassword ? (
                <svg width="22" height="22" viewBox="0 0 20 20" fill="none">
                  <path d="M2.5 10s3-6.25 7.5-6.25S17.5 10 17.5 10s-3 6.25-7.5 6.25S2.5 10 2.5 10Z" stroke="currentColor" strokeWidth="1.5" />
                  <circle cx="10" cy="10" r="2.5" stroke="currentColor" strokeWidth="1.5" />
                </svg>
              ) : (
                <svg width="22" height="22" viewBox="0 0 20 20" fill="none">
                  <path d="M3.333 3.333l13.334 13.334M8.25 8.417a2.5 2.5 0 0 0 3.333 3.333M5.633 6.017C4.017 7.2 2.5 10 2.5 10s3 6.25 7.5 6.25c1.467 0 2.783-.517 3.9-1.183M10.833 3.867c2.85.583 5.334 4.05 6.667 6.133-.617 1.017-1.483 2.3-2.6 3.367" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                </svg>
              )}
            </button>
          }
        />

        <div className="flex items-center justify-between pt-1">
          <Link
            href="/forgot-password"
            className="text-[14px] font-light text-brand-500 hover:text-brand-600 dark:text-brand-400 dark:hover:text-brand-300 transition-colors"
          >
            Esqueceu sua senha?
          </Link>
          <Toggle checked={remember} onChange={setRemember} label="Lembrar meus dados" />
        </div>

        <button type="submit" disabled={isSubmitting} className={ctaClass + " mt-2"}>
          {isSubmitting ? "Entrando..." : "Entrar"}
        </button>

        <div className="relative -my-1.5">
          <div className="absolute inset-0 flex items-center">
            <div className="w-full border-t border-gray-200 dark:border-gray-800" />
          </div>
          <div className="relative flex justify-center">
            <span className="bg-white dark:bg-gray-950 px-3 text-[12px] font-light text-[#9f9fa9]">
              ou
            </span>
          </div>
        </div>

        <button
          type="button"
          onClick={handleGoogleClick}
          disabled={googleSubmitting || isSubmitting}
          className="w-full h-12 inline-flex items-center justify-center gap-3 rounded-full border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900/40 px-4 py-2 text-[14px] font-medium text-gray-800 dark:text-gray-200 transition-colors hover:border-gray-300 dark:hover:border-gray-700 disabled:opacity-50 disabled:cursor-not-allowed"
        >
          <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M18.7511 10.1944C18.7511 9.47495 18.6915 8.94995 18.5626 8.40552H10.1797V11.6527H15.1003C15.0011 12.4597 14.4654 13.675 13.2749 14.4916L13.2582 14.6003L15.9087 16.6126L16.0924 16.6305C17.7787 15.1041 18.7511 12.8583 18.7511 10.1944Z" fill="#4285F4" />
            <path d="M10.1788 18.75C12.5895 18.75 14.6133 17.9722 16.0915 16.6305L13.274 14.4916C12.5201 15.0068 11.5081 15.3666 10.1788 15.3666C7.81773 15.3666 5.81379 13.8402 5.09944 11.7305L4.99473 11.7392L2.23868 13.8295L2.20264 13.9277C3.67087 16.786 6.68674 18.75 10.1788 18.75Z" fill="#34A853" />
            <path d="M5.10014 11.7305C4.91165 11.186 4.80257 10.6027 4.80257 9.99992C4.80257 9.3971 4.91165 8.81379 5.09022 8.26935L5.08523 8.1534L2.29464 6.02954L2.20333 6.0721C1.5982 7.25823 1.25098 8.5902 1.25098 9.99992C1.25098 11.4096 1.5982 12.7415 2.20333 13.9277L5.10014 11.7305Z" fill="#FBBC05" />
            <path d="M10.1789 4.63331C11.8554 4.63331 12.9864 5.34303 13.6312 5.93612L16.1511 3.525C14.6035 2.11528 12.5895 1.25 10.1789 1.25C6.68676 1.25 3.67088 3.21387 2.20264 6.07218L5.08953 8.26943C5.81381 6.15972 7.81776 4.63331 10.1789 4.63331Z" fill="#EB4335" />
          </svg>
          {googleSubmitting ? "Autenticando..." : "Entrar com Google"}
        </button>
      </form>

      {/* Google Identity Services — carrega de forma assíncrona (defer).
          afterInteractive garante que carregue logo após hydration sem
          bloquear o LCP do form. Quando carrega, dispara initGoogle() que
          é idempotente via initializedRef. */}
      <Script
        src="https://accounts.google.com/gsi/client"
        strategy="afterInteractive"
        onLoad={initGoogle}
      />
    </div>
  );
}
