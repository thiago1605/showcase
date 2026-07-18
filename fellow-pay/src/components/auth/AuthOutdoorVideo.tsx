import Image from "next/image";

export default function AuthOutdoorVideo() {
  return (
    <Image
      src="/images/fellow/image-fellow-pay-login-banner-gradient-1.png"
      alt="Fellow Pay"
      fill
      priority
      sizes="(min-width: 1024px) 50vw, 0px"
      aria-label="Fellow Pay — banner institucional"
      className="absolute inset-0 h-full w-full object-cover"
    />
  );
}
