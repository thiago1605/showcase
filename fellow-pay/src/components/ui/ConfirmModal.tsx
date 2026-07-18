"use client";
import React, { useMemo, useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";

interface ConfirmModalProps {
  isOpen: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  variant?: "danger" | "default";
  isLoading?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  /**
   * Exige que o usuário digite um código de verificação aleatório antes de
   * habilitar o botão de confirmação. Defesa contra cliques acidentais em
   * ações destrutivas (delete, disable). Default: false.
   */
  requireCode?: boolean;
}

// Caracteres usados no código — sem ambíguos (0/O, 1/I/L) pra reduzir erros de digitação.
const CODE_CHARS = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
const CODE_LENGTH = 6;

function generateCode(): string {
  let out = "";
  const buf = new Uint32Array(CODE_LENGTH);
  crypto.getRandomValues(buf);
  for (let i = 0; i < CODE_LENGTH; i++) {
    out += CODE_CHARS[buf[i] % CODE_CHARS.length];
  }
  return out;
}

export function ConfirmModal(props: ConfirmModalProps) {
  // Wrapper público: gate sem hooks. Quando isOpen flipa false→true, o
  // <Body> recém-monta com state limpo (código novo, campo "typed" vazio),
  // dispensando o useEffect de reset que disparava warning de set-state-in-effect.
  if (!props.isOpen) return null;
  return <ConfirmModalBody {...props} />;
}

function ConfirmModalBody({
  title,
  message,
  confirmLabel = "Confirmar",
  cancelLabel = "Cancelar",
  variant = "default",
  isLoading = false,
  onConfirm,
  onCancel,
  requireCode = false,
}: ConfirmModalProps) {
  // Initializer lazy: gera código UMA vez por mount (= por abertura do modal).
  const [code] = useState<string>(() => (requireCode ? generateCode() : ""));
  const [typed, setTyped] = useState<string>("");

  // Bloqueia scroll da page enquanto o modal estiver aberto.
  useScrollLock(true);

  const codeMatches = useMemo(
    () => !requireCode || typed.trim().toUpperCase() === code,
    [requireCode, typed, code]
  );

  const confirmClass = variant === "danger"
    ? "bg-error-600 hover:bg-error-700 text-white"
    : "bg-brand-500 hover:bg-brand-600 text-white";

  return (
    <div className="fixed inset-0 z-[100000] flex items-center justify-center p-4">
      <div
        className="absolute inset-0 bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in"
        onClick={isLoading ? undefined : onCancel}
        aria-hidden="true"
      />
      <div className="relative w-full max-w-sm rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
        <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">{title}</h3>
        <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">{message}</p>

        {requireCode && (
          <div className="mb-5 space-y-2">
            <p className="text-xs text-gray-600 dark:text-gray-400">
              Para confirmar, digite o código abaixo:
            </p>
            <div className="rounded-lg border border-gray-200 bg-gray-50 dark:border-gray-700 dark:bg-gray-800 px-3 py-2 text-center">
              <span className="font-mono text-lg font-semibold tracking-widest text-gray-900 dark:text-white select-all">
                {code}
              </span>
            </div>
            <input
              type="text"
              value={typed}
              onChange={(e) => setTyped(e.target.value.toUpperCase())}
              autoFocus
              autoComplete="off"
              spellCheck={false}
              maxLength={CODE_LENGTH}
              placeholder="Digite o código"
              aria-label="Código de verificação"
              className={`w-full rounded-lg border px-3 py-2 text-sm font-mono tracking-widest text-center focus:outline-none ${
                typed.length === 0
                  ? "border-gray-200 dark:border-gray-700"
                  : codeMatches
                    ? "border-success-500 dark:border-success-500"
                    : "border-error-500 dark:border-error-500"
              } dark:bg-gray-800 dark:text-white`}
            />
          </div>
        )}

        <div className="flex justify-end gap-3">
          <button
            onClick={onCancel}
            disabled={isLoading}
            className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800 disabled:opacity-50"
          >
            {cancelLabel}
          </button>
          <button
            onClick={onConfirm}
            disabled={isLoading || !codeMatches}
            className={`rounded-lg px-4 py-2 text-sm font-medium disabled:opacity-50 disabled:cursor-not-allowed ${confirmClass}`}
          >
            {isLoading ? "..." : confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
