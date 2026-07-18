import React, { FC, useId } from "react";

/**
 * Converte um label em slug pra usar como ID determinístico. Usar slugs ao invés
 * de `useId()` puro evita hydration mismatch — `useId` depende da posição no
 * tree React, e qualquer divergência SSR/client (ex: hook que lê localStorage)
 * troca a numeração inteira. Slug baseado no texto do label é estável por
 * definição: o mesmo label sempre vira o mesmo id, antes ou depois da
 * hidratação.
 */
function slugifyLabel(label: string): string {
  return label
    .toLowerCase()
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "") // strip diacríticos
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

interface InputProps {
  type?: "text" | "number" | "email" | "password" | "date" | "time" | "tel" | string;
  id?: string;
  name?: string;
  placeholder?: string;
  defaultValue?: string | number;
  /** Controlled mode — if set, the input becomes controlled and `defaultValue` is ignored. */
  value?: string | number;
  onChange?: (e: React.ChangeEvent<HTMLInputElement>) => void;
  /**
   * Quando passado, renderiza no estilo "stacked" (label flutuante dentro do
   * container) — mesmo padrão visual do SignInForm. Sem label, cai no estilo
   * bare (apenas input com borda).
   */
  label?: string;
  /** Slot opcional à direita do input (ex: botão de toggle de senha). */
  rightSlot?: React.ReactNode;
  className?: string;
  min?: string;
  max?: string;
  step?: number;
  maxLength?: number;
  minLength?: number;
  autoFocus?: boolean;
  autoComplete?: string;
  inputMode?: "none" | "text" | "tel" | "url" | "email" | "numeric" | "decimal" | "search";
  disabled?: boolean;
  required?: boolean;
  success?: boolean;
  error?: boolean;
  hint?: string; // Optional hint text
}

const Input: FC<InputProps> = ({
  type = "text",
  id,
  name,
  placeholder,
  defaultValue,
  value,
  onChange,
  label,
  rightSlot,
  className = "",
  min,
  max,
  step,
  maxLength,
  minLength,
  autoFocus,
  autoComplete,
  inputMode,
  disabled = false,
  required = false,
  success = false,
  error = false,
  hint,
}) => {
  // useId() chamado SEMPRE pra respeitar rules of hooks. É usado como fallback
  // só quando não há label nem id explícito — nesses casos não há `htmlFor` pra
  // amarrar, então a hydration consistency importa menos. Quando há label,
  // preferimos slug determinístico pra evitar mismatch SSR/client.
  const generatedId = useId();
  const fieldId = id ?? (label ? `field-${slugifyLabel(label)}` : generatedId);

  // Estado visual (focus-within mexe na borda do container; error/success ganham
  // tinta antes mesmo de focar, pra feedback inline).
  const containerBorder = error
    ? "border-error-500 focus-within:border-error-500"
    : success
    ? "border-success-500 focus-within:border-success-500"
    : "border-gray-200/80 dark:border-gray-800 focus-within:border-brand-500 dark:focus-within:border-brand-500";

  const labelColor = error
    ? "text-error-600 dark:text-error-400"
    : success
    ? "text-success-600 dark:text-success-400"
    : "text-gray-500 dark:text-gray-400 group-focus-within:text-brand-600 dark:group-focus-within:text-brand-400";

  // Estilo "stacked": label flutuante dentro do container preenchido.
  if (label) {
    return (
      <div>
        <div
          className={`group relative h-14 flex flex-col justify-center rounded-2xl bg-white dark:bg-gray-900/60 px-3 transition-colors border ${containerBorder} ${
            disabled ? "opacity-60 cursor-not-allowed" : ""
          }`}
        >
          <label
            htmlFor={fieldId}
            className={`block text-[12px] font-light transition-colors ${labelColor}`}
          >
            {label}
            {required && <span aria-hidden="true" className="ml-0.5 text-brand-500">*</span>}
          </label>
          <input
            id={fieldId}
            type={type}
            name={name}
            placeholder={placeholder}
            {...(value !== undefined ? { value } : { defaultValue })}
            onChange={onChange}
            min={min}
            max={max}
            step={step}
            maxLength={maxLength}
            minLength={minLength}
            autoFocus={autoFocus}
            autoComplete={autoComplete}
            inputMode={inputMode}
            disabled={disabled}
            required={required}
            className={`w-full bg-transparent text-[14px] font-light text-gray-900 dark:text-white placeholder:text-gray-400 dark:placeholder:text-gray-500 focus:outline-none focus:ring-0 disabled:cursor-not-allowed ${
              rightSlot ? "pr-8" : ""
            } ${className}`}
          />
          {rightSlot && (
            <div className="absolute right-3 top-1/2 -translate-y-1/2">{rightSlot}</div>
          )}
        </div>
        {hint && (
          <p
            className={`mt-1.5 text-xs ${
              error ? "text-error-500" : success ? "text-success-500" : "text-gray-500"
            }`}
          >
            {hint}
          </p>
        )}
      </div>
    );
  }

  // Estilo "bare": input com borda própria, pra contextos sem label (mantém
  // compat com chamadas legadas que usam <Label>... + <Input>... separados).
  let inputClasses = `h-11 w-full rounded-lg border appearance-none px-4 py-2.5 text-sm shadow-theme-xs placeholder:text-gray-400 focus:outline-hidden focus:ring-3 dark:bg-gray-900 dark:text-white/90 dark:placeholder:text-white/30 dark:focus:border-brand-800 ${className}`;
  if (disabled) {
    inputClasses += ` text-gray-500 border-gray-300 cursor-not-allowed dark:bg-gray-800 dark:text-gray-400 dark:border-gray-700`;
  } else if (error) {
    inputClasses += ` text-error-800 border-error-500 focus:ring-3 focus:ring-error-500/10 dark:text-error-400 dark:border-error-500`;
  } else if (success) {
    inputClasses += ` text-success-500 border-success-400 focus:ring-success-500/10 focus:border-success-300 dark:text-success-400 dark:border-success-500`;
  } else {
    inputClasses += ` bg-transparent text-gray-800 border-gray-300 focus:border-brand-500 focus:ring-3 focus:ring-brand-500/10 dark:border-gray-700 dark:bg-gray-900 dark:text-white/90 dark:focus:border-brand-500`;
  }

  return (
    <div className="relative">
      <input
        id={fieldId}
        type={type}
        name={name}
        placeholder={placeholder}
        {...(value !== undefined ? { value } : { defaultValue })}
        onChange={onChange}
        min={min}
        max={max}
        step={step}
        autoFocus={autoFocus}
        autoComplete={autoComplete}
        inputMode={inputMode}
        disabled={disabled}
        required={required}
        className={inputClasses}
      />
      {hint && (
        <p
          className={`mt-1.5 text-xs ${
            error ? "text-error-500" : success ? "text-success-500" : "text-gray-500"
          }`}
        >
          {hint}
        </p>
      )}
    </div>
  );
};

export default Input;
