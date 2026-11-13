import * as React from 'react';
import { Check, Copy, Download } from 'lucide-react';

import { extensionDownloadUrl, getExtensionInfo, type ExtensionInfo, type ExtensionPackage } from '@/api/extension';
import { buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';

export function ExtensionPage() {
  const [info, setInfo] = React.useState<ExtensionInfo | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    const controller = new AbortController();
    getExtensionInfo(controller.signal)
      .then(setInfo)
      .catch((cause) => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : String(cause)); });
    return () => controller.abort();
  }, []);

  const chrome = info?.packages.find((pkg) => pkg.browser === 'chrome');
  const firefox = info?.packages.find((pkg) => pkg.browser === 'firefox');

  return (
    <section className="mx-auto flex w-full max-w-3xl flex-col gap-6 p-4 sm:p-6">
      <div className="grid gap-2">
        <h1 className="text-2xl font-semibold">Browser extension</h1>
        <p className="text-muted-foreground max-w-prose text-sm">
          Save pictures, videos and whole comic pages from any site straight into nodeck. Right-click an image or a
          video and choose <b className="text-foreground">Save to nodeck…</b>: the title, tags the site shows (artists,
          characters, series, general tags), the site as a source and the page link are filled in, and you can edit
          them before saving. For a manga chapter or a gallery, click the extension&apos;s toolbar button: it collects
          the page&apos;s images in order, and you remove extra ones, reorder pages and save them as a comic or a set.
        </p>
        {info ? (
          <p className="text-muted-foreground text-sm">
            The downloaded copy is set to save to <code className="text-foreground">{info.serverUrl}</code>; you can
            change it in the extension&apos;s settings.
          </p>
        ) : null}
        {error ? <p className="text-destructive text-sm">Could not load the extension packages: {error}</p> : null}
      </div>

      <InstallCard
        title="Chrome, Edge, Brave and other Chromium browsers"
        pkg={chrome}
        loading={!info && !error}
        steps={[
          <>Download the zip and unpack it into a folder you keep — the browser runs the extension from there.</>,
          <>Open <CopyText text="chrome://extensions" /> (in Edge: <CopyText text="edge://extensions" />) and turn on
            <b> Developer mode</b>.</>,
          <>Click <b>Load unpacked</b> and choose the unpacked folder (the one that contains <code>manifest.json</code>).</>,
          <>The extension&apos;s settings open: click <b>Save and test</b>. Pin the nodeck button to the toolbar if you like.</>,
        ]}
        note={<>
          Chrome installs packaged extensions only from the Chrome Web Store (or through company policy), so a private
          extension like this one is loaded unpacked. It stays installed across restarts; Chrome may remind you that a
          developer-mode extension is on. To update, download again, replace the folder&apos;s contents and press the
          reload button on the extension&apos;s card.
        </>}
      />

      <InstallCard
        title="Firefox"
        pkg={firefox}
        loading={!info && !error}
        steps={[
          <>To try it: open <CopyText text="about:debugging#/runtime/this-firefox" />, click <b>Load Temporary
            Add-on…</b> and pick the downloaded zip. Firefox removes it when it restarts.</>,
          <>To keep it: Firefox only installs add-ons signed by Mozilla. Either sign this zip for yourself as an
            unlisted add-on on <a className="text-primary underline-offset-4 hover:underline" href="https://addons.mozilla.org/developers/" target="_blank" rel="noreferrer">addons.mozilla.org</a> (free,
            needs a Mozilla account) and install the signed file, or use Firefox Developer Edition, Nightly or ESR with
            <CopyText text="xpinstall.signatures.required" /> set to <code>false</code> in <code>about:config</code> and
            install the zip from <b>about:addons → ⚙ → Install Add-on From File</b>.</>,
        ]}
        note={<>Firefox 128 or newer.</>}
      />

      <div className="text-muted-foreground grid gap-2 text-sm">
        <h2 className="text-foreground text-base font-semibold">Why an extension and not a bookmarklet</h2>
        <p>
          A bookmarklet runs inside the site you are on. Browsers block an <code>https</code> site from sending files
          to nodeck at a plain <code>http</code> address on your network, and a site cannot read images from other
          servers. The extension runs separately with permission to fetch the images and to talk to nodeck, so it
          works on any site.
        </p>
      </div>
    </section>
  );
}

function InstallCard({ title, pkg, loading, steps, note }: {
  title: string;
  pkg: ExtensionPackage | undefined;
  loading: boolean;
  steps: React.ReactNode[];
  note: React.ReactNode;
}) {
  return (
    <div className="bg-card ring-foreground/10 grid gap-4 rounded-xl p-4 ring-1 sm:p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-base font-semibold">{title}</h2>
        {loading ? <Skeleton className="h-9 w-40" /> : pkg?.available ? (
          <a href={extensionDownloadUrl(pkg)} download className={buttonVariants()}>
            <Download /> Download{pkg.version ? ` ${pkg.version}` : ''}
          </a>
        ) : (
          <span className="text-muted-foreground text-sm">Not included in this build</span>
        )}
      </div>
      <ol className="grid list-decimal gap-2 pl-5 text-sm">
        {steps.map((step, index) => <li key={index}>{step}</li>)}
      </ol>
      <p className="text-muted-foreground text-xs">{note}</p>
    </div>
  );
}

/** Browsers do not open chrome:// or about: links from a web page, so the address is offered for copying. */
function CopyText({ text }: { text: string }) {
  const [copied, setCopied] = React.useState(false);
  async function copy() {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
      } else {
        const area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.append(area);
        area.select();
        document.execCommand('copy');
        area.remove();
      }
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch { /* the address stays selectable */ }
  }
  return (
    <button type="button" onClick={copy} title="Copy"
      className={cn('bg-muted text-foreground mx-0.5 inline-flex items-center gap-1 rounded px-1.5 py-0.5 align-baseline font-mono text-xs',
        'hover:bg-muted/70 select-all')}>
      {text}
      {copied ? <Check className="size-3" /> : <Copy className="size-3" />}
    </button>
  );
}
