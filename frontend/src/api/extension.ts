const baseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

export type ExtensionBrowser = 'chrome' | 'firefox';
export type ExtensionPackage = { browser: ExtensionBrowser; available: boolean; version: string | null; url: string };
export type ExtensionInfo = { serverUrl: string; packages: ExtensionPackage[] };

export async function getExtensionInfo(signal?: AbortSignal): Promise<ExtensionInfo> {
  const response = await fetch(`${baseUrl}/api/extension`, { signal });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? 'Request failed.');
  return data as ExtensionInfo;
}

export const extensionDownloadUrl = (pkg: ExtensionPackage) => `${baseUrl}${pkg.url}`;
