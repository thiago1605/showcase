"use client";

import { useEffect, useRef, useState } from "react";

/**
 * Componente de upload de imagem com preview LOCAL — sem rede.
 *
 * Decisão importante: o componente NÃO faz upload. Ele só gerencia:
 *  - O arquivo selecionado (File) — fica em memória do browser
 *  - O preview (via `URL.createObjectURL` — instantâneo, sem network)
 *
 * Cabe ao FORM PAI fazer o upload no momento do submit. Isso evita:
 *  - Arquivos órfãos no storage se o usuário cancelar/recarregar
 *  - Network roundtrip prematuro (preview vira instantâneo)
 *  - Upload de arquivos que o usuário sequer queria salvar
 *
 * Estado de exibição (em ordem de precedência):
 *  1. `pendingFile` (File novo selecionado nessa sessão) — preview via objectURL
 *  2. `existingUrl` (URL salva no recurso) — img direto
 *  3. Empty state (dropzone vazio)
 *
 * Cleanup de objectURLs: usa useEffect para revogar a URL quando o file muda
 * (evita leak de memória — cada `createObjectURL` aloca um slot que precisa
 * ser liberado).
 */
interface ImageUploadProps {
  /** URL atual (já salva no backend). Se ambos existingUrl e pendingFile existirem,
   *  pendingFile ganha (representa edição não-confirmada). */
  existingUrl: string | null;
  /** Arquivo selecionado mas ainda não enviado pro backend. */
  pendingFile: File | null;
  /** Callback quando user seleciona um novo arquivo no picker. */
  onPickFile: (file: File) => void;
  /** Callback quando user clica "Remover" — limpa pendingFile + existingUrl. */
  onRemove: () => void;
  accept?: string;
  maxBytes?: number;
  emptyLabel?: string;
  hint?: string;
  /** Tailwind aspect-ratio (ex: "16/9"). Default "16/9" — match com card do marketplace. */
  aspect?: string;
  /** Cap horizontal do container — preview do tamanho real do card. Default 480px. */
  maxWidth?: number;
  disabled?: boolean;
}

export function ImageUpload({
  existingUrl,
  pendingFile,
  onPickFile,
  onRemove,
  accept = "image/png,image/jpeg,image/webp",
  maxBytes = 5 * 1024 * 1024,
  emptyLabel = "Clique para enviar uma imagem",
  hint = "PNG, JPEG ou WEBP até 5 MB",
  aspect = "16/9",
  maxWidth = 480,
  disabled = false,
}: ImageUploadProps) {
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [error, setError] = useState<string | null>(null);

  // objectURL do pendingFile para preview local (sem rede). Recria sempre
  // que o file muda; revoga o anterior para não vazar memória.
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!pendingFile) {
      setObjectUrl(null);
      return;
    }
    const url = URL.createObjectURL(pendingFile);
    setObjectUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [pendingFile]);

  // URL efetiva exibida no <img>: precedência baseada em pendingFile (a
  // PROP), não em objectUrl (o state derivado). Motivo: race condition.
  //
  // Quando o pai zera pendingFile (ex: após save bem-sucedido) e simultânea-
  // mente popula existingUrl com a URL nova vinda do servidor, o React
  // batcha os updates. O useState `objectUrl` deste componente, porém,
  // ainda guarda o blob URL antigo na primeira re-render — o useEffect que
  // revoga e zera só roda DEPOIS do render.
  //
  // Se usássemos `objectUrl ?? existingUrl`, essa render intermediária faria
  // o <img> apontar para um blob URL prestes a ser revogado. O browser falha
  // silenciosamente (zero HTTP request, broken icon) e quando o useEffect
  // finalmente roda, já é tarde — a img cacheou a falha.
  //
  // Solução: a prop `pendingFile` é a fonte da verdade. Se está null,
  // ignora objectUrl (mesmo que momentaneamente exista) e usa existingUrl.
  const previewUrl = pendingFile ? objectUrl : existingUrl;

  function openPicker() {
    if (disabled) return;
    inputRef.current?.click();
  }

  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    setError(null);
    const file = e.target.files?.[0];
    // Reseta o input para que o mesmo arquivo possa ser selecionado de novo
    // (ex: usuário removeu e quer subir o mesmo arquivo).
    e.target.value = "";
    if (!file) return;

    // Validação client-side básica (mime + tamanho). Backend faz a real
    // (magic bytes), mas client-side dá feedback imediato.
    if (file.size > maxBytes) {
      setError(`Arquivo excede ${(maxBytes / (1024 * 1024)).toFixed(0)} MB.`);
      return;
    }
    if (!file.type.startsWith("image/")) {
      setError("Selecione uma imagem (PNG, JPEG ou WEBP).");
      return;
    }

    onPickFile(file);
  }

  function handleRemove() {
    setError(null);
    onRemove();
  }

  return (
    <div>
      <input
        ref={inputRef}
        type="file"
        accept={accept}
        onChange={handleFile}
        className="hidden"
        disabled={disabled}
      />

      {previewUrl ? (
        <div
          className="relative rounded-lg overflow-hidden border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-900 group"
          style={{ aspectRatio: aspect, maxWidth }}
        >
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={previewUrl}
            alt="Preview"
            className="w-full h-full object-cover"
          />

          {/* Badge "preview local" — Liquid Glass: backdrop-blur pesado +
              transparência alta + ring-inset sutil para dar volume sem opacar.
              Compacto (texto único curto), pill-shape, sem cor agressiva. */}
          {objectUrl && (
            <span
              className="
                absolute top-2 left-2 inline-flex items-center gap-1.5
                px-2.5 py-1 rounded-full text-[10px] font-medium tracking-tight
                bg-black/30 dark:bg-black/40 text-white
                backdrop-blur-2xl backdrop-saturate-150
                ring-1 ring-inset ring-white/15
                shadow-lg shadow-black/20
              "
            >
              <span aria-hidden="true" className="w-1.5 h-1.5 rounded-full bg-warning-400 animate-pulse" />
              Não salvo
            </span>
          )}

          {/* Botões "Liquid Glass" — pill compacto, transparente com blur
              pesado, hover sutil. Aparecem com fade-in ao hover do container
              (group-hover) para não competir com a imagem na visualização
              padrão. Mantemos sempre clicáveis (não opacity-0 que perde click). */}
          <div
            className="
              absolute top-2 right-2 flex gap-1.5
              opacity-90 group-hover:opacity-100 transition-opacity
            "
          >
            {/*
              Liquid Bubble Animation — emula a interação dos botões iOS 18 / VisionOS:
                - hover: scale-up sutil (1.05) com easing curto (a "bolha incha")
                - press (active): scale-down (0.88) — squish elástico, parece líquido
                - release: a curva cubic-bezier(0.34, 1.56, 0.64, 1) tem OVERSHOOT
                  (passa do 1.0 e volta) — efeito mola, característico da Apple
                - brightness no press: levíssimo aumento, simula o highlight de toque
              `will-change-transform` força composição GPU para animação fluida.
            */}
            <button
              type="button"
              onClick={openPicker}
              disabled={disabled}
              aria-label="Trocar imagem"
              title="Trocar imagem"
              className="
                h-8 px-3 inline-flex items-center justify-center gap-1.5
                rounded-full
                bg-white/15 dark:bg-black/30 text-white text-[11px] font-medium
                backdrop-blur-2xl backdrop-saturate-150
                ring-1 ring-inset ring-white/20
                shadow-lg shadow-black/20
                hover:bg-white/25 dark:hover:bg-black/45 hover:scale-105 hover:ring-white/30
                active:scale-[0.88] active:brightness-110
                disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:scale-100
                transition-all duration-300 ease-[cubic-bezier(0.34,1.56,0.64,1)]
                will-change-transform
              "
            >
              {/* Pencil — universalmente reconhecido como "editar/modificar". */}
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M17 3a2.85 2.85 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z" />
              </svg>
              Trocar
            </button>
            <button
              type="button"
              onClick={handleRemove}
              disabled={disabled}
              aria-label="Remover imagem"
              title="Remover"
              className="
                h-8 w-8 inline-flex items-center justify-center
                rounded-full
                bg-white/15 dark:bg-black/30 text-white
                backdrop-blur-2xl backdrop-saturate-150
                ring-1 ring-inset ring-white/20
                shadow-lg shadow-black/20
                hover:bg-error-500/40 hover:ring-error-300/40 hover:scale-105
                active:scale-[0.85] active:brightness-110
                disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:scale-100
                transition-all duration-300 ease-[cubic-bezier(0.34,1.56,0.64,1)]
                will-change-transform
              "
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M3 6h18" />
                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
                <path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                <line x1="10" y1="11" x2="10" y2="17" />
                <line x1="14" y1="11" x2="14" y2="17" />
              </svg>
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          onClick={openPicker}
          disabled={disabled}
          style={{ aspectRatio: aspect, maxWidth }}
          className="w-full flex flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed border-gray-300 dark:border-gray-700 bg-gray-50/50 dark:bg-gray-900/30 hover:bg-gray-50 dark:hover:bg-gray-900/50 hover:border-gray-400 dark:hover:border-gray-600 transition-colors text-gray-500 dark:text-gray-400 disabled:opacity-50"
        >
          <svg
            width="28"
            height="28"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <circle cx="9" cy="9" r="2" />
            <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
          </svg>
          <span className="text-sm font-medium">{emptyLabel}</span>
          <span className="text-[11px] text-gray-400 dark:text-gray-500">{hint}</span>
        </button>
      )}

      {error && (
        <p className="mt-2 text-xs text-error-700 dark:text-error-400">{error}</p>
      )}
    </div>
  );
}
