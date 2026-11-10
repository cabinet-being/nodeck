import * as React from 'react';
import { ArrowDown, ArrowUp, CircleHelp, Pause, X, Plus, RotateCw, Trash2, Upload } from 'lucide-react';

import { CardHttpError, createCard, createCollectionFromCards, getCard, getCardAnalysis, getCardCreationRequest, findDuplicatesByHash, updateCollectionChildren, type CardAnalysis, type Card } from '@/api/cards';
import { setCardSource, setCardTag } from '@/api/catalog';
import { BulkImportCatalogPicker, type BulkImportCatalogSelection } from '@/components/bulk-import-catalog-picker';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

type ItemStatus = 'queued' | 'uploading' | 'created' | 'error' | 'needs-review';
type MetadataStatus = 'pending' | 'assigning' | 'assigned' | 'error';
type FileDescriptor = { name: string; size: number; lastModified: number; type: string };
type ImportItem = {
  id: number;
  file?: File;
  descriptor: FileDescriptor;
  fingerprint?: string;
  fingerprintUnavailable?: boolean;
  title: string;
  previewUrl?: string;
  groupId: number | null;
  status: ItemStatus;
  cardId?: number;
  creationKey?: string;
  creationAttempted?: boolean;
  error?: string;
  possibleDuplicate?: boolean;
  sha256?: string;
  hashSkipped?: boolean;
  duplicateCardIds?: number[];
  duplicateOfName?: string;
  uploadAnyway?: boolean;
  metadataStatus?: MetadataStatus;
  metadataError?: string;
  assignedTagIds?: number[];
  sourceAssigned?: boolean;
  analysis?: CardAnalysis | null;
  analysisError?: string;
};
type ImportGroup = {
  id: number;
  type: 'comic' | 'set';
  title: string;
  cardId?: number;
  creationKey?: string;
  creationAttempted?: boolean;
  plannedChildCardIds?: number[];
  error?: string;
  needsReview?: boolean;
  needsSync?: boolean;
  syncedChildCardIds?: number[];
  metadataStatus?: MetadataStatus;
  metadataError?: string;
  assignedTagIds?: number[];
  sourceAssigned?: boolean;
};

type SavedDraft = {
  version: 1;
  items: Omit<ImportItem, 'file' | 'previewUrl' | 'analysis' | 'analysisError'>[];
  groups: ImportGroup[];
  catalogSelection: BulkImportCatalogSelection;
  selectionLocked: boolean;
  catalogPlan: BulkImportCatalogSelection | null;
};

const draftKey = 'nodeck.bulk-import.draft.v1';
const emptySelection: BulkImportCatalogSelection = { tags: [], source: null, originalUrl: '' };

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isPositiveId(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function isUuid(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value);
}

function isCatalogSelection(value: unknown): value is BulkImportCatalogSelection {
  if (!isRecord(value) || !Array.isArray(value.tags) || typeof value.originalUrl !== 'string') return false;
  if (!value.tags.every((tag: unknown) => isRecord(tag) && isPositiveId(tag.id) && typeof tag.name === 'string')) return false;
  return value.source === null || (isRecord(value.source) && isPositiveId(value.source.id)
    && typeof value.source.name === 'string');
}

function readDraft(): { draft: SavedDraft | null; warning: string | null } {
  try {
    const raw = window.localStorage.getItem(draftKey);
    if (!raw) return { draft: null, warning: null };
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) throw new Error('Invalid draft');
    const draft = parsed as SavedDraft;
    if (draft.version !== 1 || !Array.isArray(draft.items) || !Array.isArray(draft.groups)
      || !isCatalogSelection(draft.catalogSelection) || typeof draft.selectionLocked !== 'boolean'
      || (draft.catalogPlan !== null && !isCatalogSelection(draft.catalogPlan))) throw new Error('Invalid draft');
    for (const item of draft.items) {
      if (!isRecord(item) || !isPositiveId(item.id) || typeof item.title !== 'string'
        || (item.groupId !== null && !isPositiveId(item.groupId))
        || !['queued', 'uploading', 'created', 'error', 'needs-review'].includes(item.status)
        || !isRecord(item.descriptor) || typeof item.descriptor.name !== 'string'
        || !Number.isSafeInteger(item.descriptor.size) || item.descriptor.size < 0
        || !Number.isSafeInteger(item.descriptor.lastModified)
        || typeof item.descriptor.type !== 'string'
        || (item.cardId !== undefined && !isPositiveId(item.cardId))
        || (item.creationKey !== undefined && !isUuid(item.creationKey))
        || (item.creationAttempted !== undefined && typeof item.creationAttempted !== 'boolean')
        || (item.assignedTagIds !== undefined && (!Array.isArray(item.assignedTagIds)
          || !item.assignedTagIds.every(isPositiveId)))) throw new Error('Invalid saved item');
      if ((item.sha256 !== undefined && (typeof item.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(item.sha256)))
        || (item.duplicateCardIds !== undefined && (!Array.isArray(item.duplicateCardIds) || !item.duplicateCardIds.every(isPositiveId)))
        || (item.duplicateOfName !== undefined && typeof item.duplicateOfName !== 'string')) throw new Error('Invalid saved hash');
      if (item.fingerprint !== undefined && (typeof item.fingerprint !== 'string'
        || !/^sample-sha256-v1:[a-f0-9]{64}$/.test(item.fingerprint))) throw new Error('Invalid sampled fingerprint');
    }
    for (const group of draft.groups) {
      if (!isRecord(group) || !isPositiveId(group.id) || !['comic', 'set'].includes(group.type)
        || typeof group.title !== 'string' || (group.cardId !== undefined && !isPositiveId(group.cardId))
        || (group.creationKey !== undefined && !isUuid(group.creationKey))
        || (group.creationAttempted !== undefined && typeof group.creationAttempted !== 'boolean')
        || (group.plannedChildCardIds !== undefined && (!Array.isArray(group.plannedChildCardIds)
          || !group.plannedChildCardIds.every(isPositiveId)))
        || (group.syncedChildCardIds !== undefined && (!Array.isArray(group.syncedChildCardIds)
          || !group.syncedChildCardIds.every(isPositiveId)))
        || (group.assignedTagIds !== undefined && (!Array.isArray(group.assignedTagIds)
          || !group.assignedTagIds.every(isPositiveId)))) throw new Error('Invalid saved group');
    }
    return { draft, warning: null };
  } catch {
    return { draft: null, warning: 'Saved local draft could not be restored. This page starts with an empty queue.' };
  }
}

function descriptorOf(file: File): FileDescriptor {
  return { name: file.name, size: file.size, lastModified: file.lastModified, type: file.type };
}

function matchesDescriptor(file: File, descriptor: FileDescriptor) {
  return file.name === descriptor.name && file.size === descriptor.size
    && file.lastModified === descriptor.lastModified && file.type === descriptor.type;
}

async function sampledFingerprint(file: File): Promise<string> {
  if (!globalThis.crypto?.subtle) throw new Error('WebCrypto unavailable');
  const sampleSize = 64 * 1024;
  const first = await file.slice(0, sampleSize).arrayBuffer();
  const last = await file.slice(Math.max(0, file.size - sampleSize)).arrayBuffer();
  const bytes = new Uint8Array(8 + first.byteLength + last.byteLength);
  new DataView(bytes.buffer).setBigUint64(0, BigInt(file.size));
  bytes.set(new Uint8Array(first), 8);
  bytes.set(new Uint8Array(last), 8 + first.byteLength);
  const digest = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', bytes));
  return `sample-sha256-v1:${Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('')}`;
}

async function tryFingerprint(file: File): Promise<string | undefined> {
  try { return await sampledFingerprint(file); } catch { return undefined; }
}

async function fingerprintFiles(files: File[]): Promise<(string | undefined)[]> {
  const results = new Array<string | undefined>(files.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(4, files.length) }, async () => {
    while (next < files.length) {
      const index = next++;
      results[index] = await tryFingerprint(files[index]);
    }
  }));
  return results;
}

const uploadConcurrency = 3;
const hashConcurrency = 2;
const maxHashBytes = 1024 * 1024 * 1024;

async function tryFullHash(file: File): Promise<string | undefined> {
  try {
    if (!globalThis.crypto?.subtle) return undefined;
    const digest = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
    return Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('');
  } catch { return undefined; }
}

function isBlockedDuplicate(item: Pick<ImportItem, 'duplicateCardIds' | 'duplicateOfName' | 'uploadAnyway' | 'cardId'>) {
  return !item.cardId && (!!item.duplicateCardIds?.length || !!item.duplicateOfName) && !item.uploadAnyway;
}

function mediaTypeLabel(descriptor: FileDescriptor): 'Image' | 'GIF' | 'Video' {
  const extension = descriptor.name.split('.').pop()?.toLowerCase() ?? '';
  const mime = descriptor.type.toLowerCase();
  if (mime === 'image/gif' || (!mime && extension === 'gif')) return 'GIF';
  if (mime.startsWith('video/') || (!mime && ['mp4', 'mov', 'webm'].includes(extension))) return 'Video';
  if (mime.startsWith('image/')) return 'Image';
  if (extension === 'gif') return 'GIF';
  return ['mp4', 'mov', 'webm'].includes(extension) ? 'Video' : 'Image';
}

const allowedExtensions = new Set(['jpg', 'jpeg', 'png', 'webp', 'gif', 'mp4', 'mov', 'webm']);

function isSupported(file: File) {
  const extension = file.name.split('.').pop()?.toLowerCase() ?? '';
  return allowedExtensions.has(extension);
}

function fileKey(file: FileDescriptor) {
  return `${file.name}\0${file.size}\0${file.lastModified}`;
}

function fileTitle(file: File) {
  return file.name.replace(/\.[^.]+$/, '').trim();
}

function storedUploadName(name: string) {
  const baseName = name.replaceAll('\\', '/').split('/').at(-1) ?? '';
  const cleaned = baseName.replace(/[\u0000-\u001f\u007f-\u009f'"]/g, '').slice(0, 120);
  return cleaned.trim() ? cleaned : 'unnamed upload';
}

function errorMessage(cause: unknown) {
  return cause instanceof Error ? cause.message : 'Request failed.';
}

function isUnconfirmedPost(cause: unknown) {
  // A 5xx response may arrive after the server committed; only 4xx responses
  // establish a retryable request failure here.
  return !(cause instanceof CardHttpError) || cause.status >= 500;
}

async function findCreationResult(scope: 'media' | 'collection', key: string, signal?: AbortSignal): Promise<Card | null> {
  try {
    return await getCardCreationRequest(scope, key, signal);
  } catch (cause) {
    if (cause instanceof CardHttpError && cause.status === 404) return null;
    throw cause;
  }
}

function creationError(cause: unknown): string {
  if (cause instanceof CardHttpError) {
    if (cause.status === 410) return 'The card created by this request was deleted. This row cannot be retried with the same key.';
    if (cause.status === 409) return 'This creation key conflicts with a changed request. Restore the original title, file, or group order.';
    if (cause.status === 425) return 'Creation is still settling. Retry in a moment with the same key.';
  }
  return `${errorMessage(cause)} Retry will check this creation key before sending the same request.`;
}

function hasSyncedChildren(group: ImportGroup, childCardIds: number[]) {
  return group.syncedChildCardIds?.length === childCardIds.length
    && childCardIds.every((cardId, index) => group.syncedChildCardIds?.[index] === cardId);
}

function parseCardId(value: string): number | null {
  if (!/^[1-9]\d*$/.test(value.trim())) return null;
  const id = Number(value.trim());
  return Number.isSafeInteger(id) ? id : null;
}

export function BulkImportPage() {
  const initial = React.useRef<ReturnType<typeof readDraft> | null>(null);
  if (!initial.current) initial.current = readDraft();
  const restored = initial.current.draft;
  const [items, setItems] = React.useState<ImportItem[]>(() => (restored?.items ?? []).map((item) => ({
    ...item, status: item.status === 'uploading' || (item.creationKey && item.status === 'needs-review')
      ? item.creationKey ? 'error' : 'needs-review' : item.status,
    metadataStatus: item.metadataStatus === 'assigning' ? 'pending' : item.metadataStatus,
  })));
  const [groups, setGroups] = React.useState<ImportGroup[]>(() => (restored?.groups ?? []).map((group) => ({
    ...group, needsReview: group.creationKey ? false : group.needsReview,
    metadataStatus: group.metadataStatus === 'assigning' ? 'pending' : group.metadataStatus,
  })));
  const [isRunning, setIsRunning] = React.useState(false);
  const [isPreparingFiles, setIsPreparingFiles] = React.useState(false);
  const [pauseRequested, setPauseRequested] = React.useState(false);
  const [cancelRequested, setCancelRequested] = React.useState(false);
  const [currentStep, setCurrentStep] = React.useState<string | null>(null);
  const [isDragging, setIsDragging] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [mediaReviewIds, setMediaReviewIds] = React.useState<Record<number, string>>({});
  const [groupReviewIds, setGroupReviewIds] = React.useState<Record<number, string>>({});
  const [reviewErrors, setReviewErrors] = React.useState<Record<string, string>>({});
  const [reconciling, setReconciling] = React.useState<string | null>(null);
  const [persistenceWarning, setPersistenceWarning] = React.useState<string | null>(initial.current.warning);
  const [catalogSelection, setCatalogSelection] = React.useState<BulkImportCatalogSelection>(restored?.catalogSelection ?? emptySelection);
  const [selectionLocked, setSelectionLocked] = React.useState(restored?.selectionLocked ?? false);
  const catalogPlan = React.useRef<BulkImportCatalogSelection | null>(restored?.catalogPlan ?? null);
  const nextId = React.useRef(Math.max(0, ...(restored?.items ?? []).map((item) => item.id), ...(restored?.groups ?? []).map((group) => group.id)) + 1);
  const pendingItemPosts = React.useRef<Set<number>>(new Set());
  const pendingGroupPosts = React.useRef<Set<number>>(new Set());
  const draftEnabled = React.useRef(true);
  const urls = React.useRef<Set<string>>(new Set());
  const itemsRef = React.useRef(items);
  const groupsRef = React.useRef(groups);
  const reconcilingRef = React.useRef(false);
  const analysisInFlight = React.useRef<Set<number>>(new Set());
  const analysisCursor = React.useRef(0);
  const mounted = React.useRef(true);
  const pauseRequestedRef = React.useRef(false);
  const cancelRequestedRef = React.useRef(false);
  const abortRef = React.useRef<AbortController | null>(null);
  const preparingFilesRef = React.useRef(false);
  const fileSelectionGeneration = React.useRef(0);
  itemsRef.current = items;
  groupsRef.current = groups;

  function saveDraft(currentItems: ImportItem[], currentGroups: ImportGroup[], selection = catalogSelection, locked = selectionLocked): boolean {
    if (!draftEnabled.current || !mounted.current) return false;
    try {
      const draft: SavedDraft = {
        version: 1,
        items: currentItems.map(({ file: _file, previewUrl: _previewUrl, analysis: _analysis, analysisError: _analysisError, ...item }) => ({
          ...item, status: pendingItemPosts.current.has(item.id) ? item.creationKey ? 'error' : 'needs-review' : item.status,
        })),
        groups: currentGroups.map((group) => ({ ...group, needsReview: !group.creationKey && (pendingGroupPosts.current.has(group.id) || group.needsReview) })),
        catalogSelection: selection,
        selectionLocked: locked,
        catalogPlan: catalogPlan.current,
      };
      window.localStorage.setItem(draftKey, JSON.stringify(draft));
      if (persistenceWarning?.startsWith('Local draft could not be saved')) setPersistenceWarning(null);
      return true;
    } catch {
      setPersistenceWarning('Local draft could not be saved (storage may be unavailable or full). This page still works, but changes may be lost on reload.');
      return false;
    }
  }

  React.useEffect(() => {
    if (!isRunning) saveDraft(items, groups);
  }, [items, groups, catalogSelection, selectionLocked, isRunning]);

  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      urls.current.forEach((url) => URL.revokeObjectURL(url));
    };
  }, []);

  const checkAnalysis = React.useCallback(async (item: ImportItem) => {
    if (!item.cardId || analysisInFlight.current.has(item.cardId) || analysisInFlight.current.size >= 4) return;
    analysisInFlight.current.add(item.cardId);
    try {
      const analysis = await getCardAnalysis(item.cardId);
      if (mounted.current) {
        setItems((current) => {
          const previous = current.find((candidate) => candidate.id === item.id);
          if (!previous || previous.analysis?.state === analysis?.state
            && previous.analysis?.attempts === analysis?.attempts
            && previous.analysis?.availableAtUtc === analysis?.availableAtUtc
            && previous.analysis?.lastError === analysis?.lastError
            && !previous.analysisError) return current;
          return current.map((candidate) => candidate.id === item.id
            ? { ...candidate, analysis, analysisError: undefined } : candidate);
        });
      }
    } catch (cause) {
      if (mounted.current) {
        setItems((current) => {
          const error = errorMessage(cause);
          if (current.find((candidate) => candidate.id === item.id)?.analysisError === error) return current;
          return current.map((candidate) => candidate.id === item.id
            ? { ...candidate, analysisError: error } : candidate);
        });
      }
    } finally {
      analysisInFlight.current.delete(item.cardId);
    }
  }, []);

  const cardIds = items.flatMap((item) => item.cardId ? [item.cardId] : []).join(',');
  React.useEffect(() => {
    if (!cardIds) return;
    const poll = () => {
      const unfinished = itemsRef.current.filter((item) => item.cardId
        && item.analysis !== null
        && item.analysis?.state !== 'done'
        && item.analysis?.state !== 'failed');
      if (unfinished.length === 0) return;
      const start = analysisCursor.current % unfinished.length;
      for (let offset = 0; offset < Math.min(4, unfinished.length); offset++) {
        void checkAnalysis(unfinished[(start + offset) % unfinished.length]);
      }
      analysisCursor.current = (start + Math.min(4, unfinished.length)) % unfinished.length;
    };
    poll();
    const interval = window.setInterval(poll, 5000);
    return () => window.clearInterval(interval);
  }, [cardIds, checkAnalysis]);

  async function addFiles(files: FileList | File[]) {
    if (isRunning || preparingFilesRef.current) return;
    draftEnabled.current = true;
    const incoming = Array.from(files);
    if (incoming.length === 0) return;
    preparingFilesRef.current = true;
    setIsPreparingFiles(true);
    const generation = fileSelectionGeneration.current;
    const fingerprints = await fingerprintFiles(incoming);
    if (!mounted.current || generation !== fileSelectionGeneration.current) return;
    preparingFilesRef.current = false;
    setIsPreparingFiles(false);
    const currentItems = itemsRef.current;
    const seenKeys = new Set(currentItems.map((item) => fileKey(item.descriptor)));
    const attached = new Set<number>();
    const ambiguous = new Set<number>();
    incoming.forEach((file, index) => {
      const descriptorRows = currentItems.filter((item) => !item.cardId && !item.file && matchesDescriptor(file, item.descriptor));
      const fingerprint = fingerprints[index];
      if (!fingerprint || descriptorRows.some((row) => !row.fingerprint)) {
        if (descriptorRows.length) ambiguous.add(index);
        return;
      }
      const matchingRows = descriptorRows.filter((row) => row.fingerprint === fingerprint);
      const matchingFiles = incoming.filter((candidate, candidateIndex) => fingerprints[candidateIndex] === fingerprint
        && matchesDescriptor(candidate, descriptorOf(file)));
      if (matchingRows.length === 1 && matchingFiles.length === 1) {
        const row = matchingRows[0];
        const previewUrl = URL.createObjectURL(file);
        urls.current.add(previewUrl);
        updateItem(row.id, { file, previewUrl, fingerprintUnavailable: false });
        attached.add(index);
      } else if (matchingRows.length > 0) {
        ambiguous.add(index);
      }
    });
    let possibleDuplicateCount = 0;
    const added = incoming.flatMap((file, index): ImportItem[] => {
      if (attached.has(index) || ambiguous.has(index)) return [];
      const descriptor = descriptorOf(file);
      const key = fileKey(descriptor);
      const possibleDuplicate = seenKeys.has(key);
      if (possibleDuplicate) possibleDuplicateCount++;
      seenKeys.add(key);
      const previewUrl = URL.createObjectURL(file);
      urls.current.add(previewUrl);
      return [{
        id: nextId.current++, file, descriptor, fingerprint: fingerprints[index],
        fingerprintUnavailable: !fingerprints[index], title: fileTitle(file), previewUrl, groupId: null,
        possibleDuplicate,
        status: isSupported(file) ? 'queued' : 'error',
        error: isSupported(file) ? undefined : 'Unsupported file. Choose JPEG, PNG, WebP, GIF, MP4, MOV, or WebM.',
      }];
    });
    setItems((current) => [...current, ...added]);
    setNotice(ambiguous.size ? `${ambiguous.size} file(s) need manual attachment to a restored row because identity could not be confirmed automatically. Use that row's Attach button.`
      : possibleDuplicateCount ? `${possibleDuplicateCount} possible duplicate(s) added to the queue. Review before uploading; matching name, size, and modified time do not prove identical content.`
        : attached.size ? `${attached.size} restored file(s) reattached after sampled fingerprint comparison.` : null);
  }

  async function attachFile(item: ImportItem, file: File) {
    if (isRunning || preparingFilesRef.current) return;
    if (!matchesDescriptor(file, item.descriptor)) {
      setNotice(`Selected file does not match ${item.descriptor.name} (name, size, modified time, or type differs).`);
      return;
    }
    const fingerprint = await tryFingerprint(file);
    if (!mounted.current || !itemsRef.current.some((candidate) => candidate.id === item.id && !candidate.file)) return;
    if (item.fingerprint && !fingerprint) {
      setNotice(`Could not verify the saved sampled fingerprint for ${item.descriptor.name}. Try again when fingerprinting is available.`);
      return;
    }
    if (item.fingerprint && fingerprint && item.fingerprint !== fingerprint) {
      setNotice(`Selected file does not match the saved sampled fingerprint for ${item.descriptor.name}. Choose the original file.`);
      return;
    }
    if (item.previewUrl) {
      URL.revokeObjectURL(item.previewUrl);
      urls.current.delete(item.previewUrl);
    }
    const previewUrl = URL.createObjectURL(file);
    urls.current.add(previewUrl);
    updateItem(item.id, { file, previewUrl, fingerprint: fingerprint ?? item.fingerprint,
      fingerprintUnavailable: !fingerprint });
    setNotice(fingerprint ? `Reattached ${item.descriptor.name}${item.fingerprint ? ' after sampled fingerprint comparison' : ' by manual selection'}.`
      : `Reattached ${item.descriptor.name} manually. Fingerprint verification was unavailable; check this file before uploading.`);
  }

  function addGroup() {
    draftEnabled.current = true;
    setGroups((current) => [...current, { id: nextId.current++, type: 'comic', title: '' }]);
  }

  function updateItem(id: number, patch: Partial<ImportItem>) {
    setItems((current) => current.map((item) => item.id === id ? { ...item, ...patch } : item));
  }

  async function reconcileMedia(itemId: number) {
    if (isRunning || reconcilingRef.current) return;
    const key = `media-${itemId}`;
    const cardId = parseCardId(mediaReviewIds[itemId] ?? '');
    if (cardId === null) {
      setReviewErrors((current) => ({ ...current, [key]: 'Enter a positive whole-number card ID.' }));
      return;
    }
    reconcilingRef.current = true;
    setReconciling(key);
    setReviewErrors((current) => ({ ...current, [key]: '' }));
    try {
      const card = await getCard(cardId);
      const item = itemsRef.current.find((candidate) => candidate.id === itemId && candidate.status === 'needs-review' && !candidate.cardId);
      if (!item) throw new Error('This row is no longer awaiting review.');
      if (itemsRef.current.some((candidate) => candidate.id !== itemId && candidate.cardId === cardId)) {
        throw new Error('This card ID is already assigned to another file in the queue.');
      }
      if (card.type !== 'media' || card.metadata?.original_file_name !== storedUploadName(item.descriptor.name)
        || card.metadata?.file_size !== item.descriptor.size) {
        throw new Error('Card type, original file name, or file size does not match this saved file.');
      }
      const nextItems = itemsRef.current.map((candidate) => candidate.id === itemId
        ? { ...candidate, cardId, status: 'created' as const, error: undefined } : candidate);
      if (!saveDraft(nextItems, groupsRef.current)) throw new Error('Could not save the reconciled draft. Free browser storage and try again.');
      itemsRef.current = nextItems;
      setItems(nextItems);
      setNotice(`Media #${cardId} linked to ${item.descriptor.name}. Name and size match; these checks do not prove full file identity. Choose Upload / retry to continue.`);
    } catch (cause) {
      setReviewErrors((current) => ({ ...current, [key]: errorMessage(cause) }));
    } finally {
      reconcilingRef.current = false;
      setReconciling(null);
    }
  }

  async function reconcileGroup(groupId: number) {
    if (isRunning || reconcilingRef.current) return;
    const key = `group-${groupId}`;
    const cardId = parseCardId(groupReviewIds[groupId] ?? '');
    if (cardId === null) {
      setReviewErrors((current) => ({ ...current, [key]: 'Enter a positive whole-number card ID.' }));
      return;
    }
    reconcilingRef.current = true;
    setReconciling(key);
    setReviewErrors((current) => ({ ...current, [key]: '' }));
    try {
      const card = await getCard(cardId);
      const group = groupsRef.current.find((candidate) => candidate.id === groupId && candidate.needsReview && !candidate.cardId);
      if (!group) throw new Error('This group is no longer awaiting review.');
      if (groupsRef.current.some((candidate) => candidate.id !== groupId && candidate.cardId === cardId)) {
        throw new Error('This card ID is already assigned to another group in the queue.');
      }
      const members = itemsRef.current.filter((item) => item.groupId === groupId);
      const childCardIds = members.flatMap((item) => item.cardId ? [item.cardId] : []);
      if (members.length < 2 || childCardIds.length !== members.length) {
        throw new Error('All planned group files must have uploaded card IDs before reconciliation.');
      }
      const actualIds = card.containedCards?.map((child) => child.id);
      if (card.type !== group.type || !actualIds || actualIds.length !== childCardIds.length
        || !childCardIds.every((id, index) => actualIds[index] === id)) {
        throw new Error('Card type or ordered child card IDs do not match this planned group.');
      }
      const nextGroups = groupsRef.current.map((candidate) => candidate.id === groupId
        ? { ...candidate, cardId, needsReview: false, needsSync: false, syncedChildCardIds: [...childCardIds], error: undefined } : candidate);
      if (!saveDraft(itemsRef.current, nextGroups)) throw new Error('Could not save the reconciled draft. Free browser storage and try again.');
      groupsRef.current = nextGroups;
      setGroups(nextGroups);
      setNotice(`${group.type} #${cardId} linked after checking its ordered child cards. Choose Upload / retry to continue.`);
    } catch (cause) {
      setReviewErrors((current) => ({ ...current, [key]: errorMessage(cause) }));
    } finally {
      reconcilingRef.current = false;
      setReconciling(null);
    }
  }

  function moveItem(id: number, direction: -1 | 1) {
    const index = items.findIndex((item) => item.id === id);
    const groupId = items[index]?.groupId;
    if (groupId == null) return;
    const groupItems = items.filter((item) => item.groupId === groupId);
    const groupIndex = groupItems.findIndex((item) => item.id === id);
    const neighbor = groupItems[groupIndex + direction];
    if (!neighbor) return;
    const otherIndex = items.findIndex((item) => item.id === neighbor.id);
    setItems((current) => {
      const next = [...current];
      [next[index], next[otherIndex]] = [next[otherIndex], next[index]];
      return next;
    });
    if (groups.some((group) => group.id === groupId && group.cardId)) {
      setGroups((current) => current.map((group) => group.id === groupId ? { ...group, needsSync: true } : group));
    }
  }

  function removeItem(item: ImportItem) {
    if (item.previewUrl) {
      URL.revokeObjectURL(item.previewUrl);
      urls.current.delete(item.previewUrl);
    }
    setItems((current) => current.filter((candidate) => candidate.id !== item.id));
  }

  async function runImport() {
    if (isRunning || preparingFilesRef.current || reconcilingRef.current) return;
    if (!catalogPlan.current) {
      catalogPlan.current = {
        tags: [...catalogSelection.tags], source: catalogSelection.source,
        originalUrl: catalogSelection.originalUrl.trim(),
      };
      setSelectionLocked(true);
      saveDraft(items, groups, catalogSelection, true);
    }
    const plan = catalogPlan.current;
    const hasCatalogAssignment = plan.tags.length > 0 || plan.source !== null;
    setIsRunning(true);
    pauseRequestedRef.current = false;
    setPauseRequested(false);
    setNotice(null);
    cancelRequestedRef.current = false;
    setCancelRequested(false);
    const controller = new AbortController();
    abortRef.current = controller;
    const signal = controller.signal;
    const stopRequested = () => !mounted.current || pauseRequestedRef.current;
    const working = items.map((item) => ({ ...item }));
    const workingGroups = groups.map((group) => ({ ...group }));
    let failures = 0;
    let paused = false;

    try {
      await checkServerDuplicates(working, signal, stopRequested);
      if (stopRequested()) paused = true;
      const candidates = working.filter((item) => !(!item.file && !item.cardId && !item.creationKey)
        && !(item.file && !isSupported(item.file)) && item.status !== 'needs-review');
      let cursor = 0;
      const inFlight = new Set<string>();
      const processItem = async (item: ImportItem) => {
        if (isBlockedDuplicate(item)) return;
        inFlight.add(item.descriptor.name);
        try {
          await processItemSteps(item);
        } finally {
          inFlight.delete(item.descriptor.name);
        }
      };
      const processItemSteps = async (item: ImportItem) => {
        if (stopRequested()) { paused = true; return; }
        if (!item.file && !item.cardId && !item.creationKey) return;
        if (item.file && !isSupported(item.file)) return;
        if (item.status === 'needs-review') return;
        if (!item.cardId) {
          setCurrentStep(`Uploading ${[...inFlight].join(', ')}`);
          updateItem(item.id, { status: 'uploading', error: undefined });
          try {
            if (item.creationKey && item.creationAttempted) {
              const found = await findCreationResult('media', item.creationKey, signal);
              if (found) {
                item.cardId = found.id;
                item.status = 'created';
                item.error = undefined;
                updateItem(item.id, { cardId: found.id, status: 'created', error: undefined });
                saveDraft(working, workingGroups, catalogSelection, true);
              }
            }
            if (!item.cardId) {
            if (!item.file) {
              item.status = 'error';
              item.error = 'Creation has not been confirmed. Attach the original file to retry with the same key.';
              updateItem(item.id, { status: 'error', error: item.error });
              return;
            }
            item.creationKey ??= globalThis.crypto.randomUUID();
            item.creationAttempted = true;
            updateItem(item.id, { creationKey: item.creationKey, creationAttempted: true });
            pendingItemPosts.current.add(item.id);
            if (!saveDraft(working, workingGroups, catalogSelection, true)) {
              pendingItemPosts.current.delete(item.id);
              item.status = 'error';
              item.error = 'Could not save the recovery marker. Free browser storage before uploading this file.';
              updateItem(item.id, { status: 'error', error: item.error });
              failures++;
              return;
            }
            const card = await createCard({ type: 'media', image: item.file, title: item.title.trim() || undefined,
              idempotencyKey: item.creationKey }, signal);
            pendingItemPosts.current.delete(item.id);
            item.cardId = card.id;
            item.status = 'created';
            item.error = undefined;
            updateItem(item.id, { cardId: card.id, status: 'created', error: undefined });
            saveDraft(working, workingGroups, catalogSelection, true);
            }
          } catch (cause) {
            pendingItemPosts.current.delete(item.id);
            if (signal.aborted && item.creationKey) {
              item.status = 'error';
              item.error = 'Cancelled while uploading; the outcome is unknown. Upload / retry will check this creation key before sending the same request again.';
              updateItem(item.id, { status: 'error', error: item.error });
              saveDraft(working, workingGroups, catalogSelection, true);
              return;
            }
            let recovered = false;
            if (item.creationKey && isUnconfirmedPost(cause)) {
              try {
                const found = await findCreationResult('media', item.creationKey);
                if (found) {
                  item.cardId = found.id;
                  item.status = 'created';
                  item.error = undefined;
                  updateItem(item.id, { cardId: found.id, status: 'created', error: undefined });
                  saveDraft(working, workingGroups, catalogSelection, true);
                  recovered = true;
                }
              } catch (lookupCause) { cause = lookupCause; }
            }
            if (!recovered && item.creationKey) {
              item.status = 'error';
              item.error = creationError(cause);
              updateItem(item.id, { status: 'error', error: item.error });
              saveDraft(working, workingGroups, catalogSelection, true);
              failures++;
              return;
            }
            if (!recovered) {
              const needsReview = isUnconfirmedPost(cause);
              item.status = needsReview ? 'needs-review' : 'error';
              item.error = needsReview
                ? `The create request could not be confirmed (${errorMessage(cause)}). The card may already exist. Check the gallery before retrying this POST.`
                : errorMessage(cause);
              updateItem(item.id, { status: item.status, error: item.error });
              saveDraft(working, workingGroups, catalogSelection, true);
              failures++;
              return;
            }
          }
        }
        if (stopRequested()) { paused = true; return; }
        if (hasCatalogAssignment && item.cardId && item.metadataStatus !== 'assigned') {
          setCurrentStep(`Assigning tags/source to ${item.descriptor.name} (${inFlight.size} file(s) in progress)`);
          updateItem(item.id, { metadataStatus: 'assigning', metadataError: undefined });
          try {
            for (const tag of plan.tags) {
              if (pauseRequestedRef.current) break;
              if (item.assignedTagIds?.includes(tag.id)) continue;
              await setCardTag(item.cardId, tag.id, signal);
              item.assignedTagIds = [...(item.assignedTagIds ?? []), tag.id];
              updateItem(item.id, { assignedTagIds: item.assignedTagIds });
              saveDraft(working, workingGroups, catalogSelection, true);
            }
            if (!pauseRequestedRef.current && plan.source && !item.sourceAssigned) {
              await setCardSource(item.cardId, plan.source.id, plan.originalUrl, signal);
              item.sourceAssigned = true;
              updateItem(item.id, { sourceAssigned: true });
              saveDraft(working, workingGroups, catalogSelection, true);
            }
            item.metadataStatus = pauseRequestedRef.current ? 'pending' : 'assigned';
            item.metadataError = undefined;
            updateItem(item.id, { metadataStatus: item.metadataStatus, metadataError: undefined });
          } catch (cause) {
            if (signal.aborted) {
              item.metadataStatus = 'pending';
              updateItem(item.id, { metadataStatus: 'pending', metadataError: undefined });
              paused = true;
              return;
            }
            item.metadataStatus = 'error';
            item.metadataError = errorMessage(cause);
            updateItem(item.id, { metadataStatus: 'error', metadataError: item.metadataError });
            failures++;
          }
        }
        if (stopRequested()) { paused = true; return; }
      };
      await Promise.all(Array.from({ length: Math.min(uploadConcurrency, candidates.length) }, async () => {
        while (cursor < candidates.length) {
          if (stopRequested()) { paused = true; return; }
          await processItem(candidates[cursor++]);
        }
      }));
      if (stopRequested()) paused = true;

      for (const group of paused ? [] : workingGroups) {
        if (stopRequested()) { paused = true; break; }
        const members = working.filter((item) => item.groupId === group.id);
        const childCardIds = members.flatMap((item) => item.cardId ? [item.cardId] : []);
        if (members.length === 0) continue;
        if (group.needsReview && !group.creationKey) continue;
        if (!group.cardId || group.needsSync || !hasSyncedChildren(group, childCardIds)) {
          if (childCardIds.length < 2 || (!group.cardId && childCardIds.length !== members.length)) {
            group.error = childCardIds.length < 2
              ? 'At least two successful media uploads are needed.'
              : 'Waiting for all assigned files to upload before creating this group. Retry failed files or remove them from this group.';
            setGroups((current) => current.map((candidate) => candidate.id === group.id ? { ...candidate, error: group.error } : candidate));
            failures++;
            continue;
          }
          try {
            setCurrentStep(`${group.cardId ? 'Updating' : 'Creating'} ${group.type} group ${group.title || group.id}`);
            let foundExistingCreation = false;
            if (group.cardId) {
              await updateCollectionChildren(group.cardId, childCardIds);
            } else {
              if (group.creationKey && group.creationAttempted) {
                const found = await findCreationResult('collection', group.creationKey);
                if (found) {
                  group.cardId = found.id;
                  foundExistingCreation = true;
                }
              }
              if (!group.cardId) {
              group.creationKey ??= globalThis.crypto.randomUUID();
              group.creationAttempted = true;
              group.plannedChildCardIds ??= [...childCardIds];
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, creationKey: group.creationKey, creationAttempted: true,
                  plannedChildCardIds: group.plannedChildCardIds } : candidate));
              pendingGroupPosts.current.add(group.id);
              if (!saveDraft(working, workingGroups, catalogSelection, true)) {
                pendingGroupPosts.current.delete(group.id);
                group.error = 'Could not save the recovery marker. Free browser storage before creating this group.';
                setGroups((current) => current.map((candidate) => candidate.id === group.id
                  ? { ...candidate, error: group.error } : candidate));
                failures++;
                continue;
              }
              const created = await createCollectionFromCards({
                type: group.type, title: group.title.trim() || undefined,
                childCardIds: group.plannedChildCardIds, idempotencyKey: group.creationKey,
              });
              pendingGroupPosts.current.delete(group.id);
              group.cardId = created.id;
              }
            }
            group.error = undefined;
            group.syncedChildCardIds = [...(foundExistingCreation ? group.plannedChildCardIds ?? childCardIds : childCardIds)];
            group.needsSync = !hasSyncedChildren(group, childCardIds);
            setGroups((current) => current.map((candidate) => candidate.id === group.id
              ? { ...candidate, cardId: group.cardId, error: undefined, needsSync: group.needsSync,
                syncedChildCardIds: group.syncedChildCardIds } : candidate));
            saveDraft(working, workingGroups, catalogSelection, true);
          } catch (cause) {
            pendingGroupPosts.current.delete(group.id);
            let recovered = false;
            if (group.creationKey && !group.cardId && isUnconfirmedPost(cause)) {
              try {
                const found = await findCreationResult('collection', group.creationKey);
                if (found) {
                  group.cardId = found.id;
                  group.needsReview = false;
                  group.error = undefined;
                  group.syncedChildCardIds = [...(group.plannedChildCardIds ?? childCardIds)];
                  group.needsSync = !hasSyncedChildren(group, childCardIds);
                  setGroups((current) => current.map((candidate) => candidate.id === group.id
                    ? { ...candidate, cardId: found.id, needsReview: false, error: undefined,
                      needsSync: group.needsSync, syncedChildCardIds: group.syncedChildCardIds } : candidate));
                  saveDraft(working, workingGroups, catalogSelection, true);
                  recovered = true;
                }
              } catch (lookupCause) { cause = lookupCause; }
            }
            if (!recovered && group.creationKey && !group.cardId) {
              group.needsReview = false;
              group.error = creationError(cause);
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, error: group.error, needsReview: false } : candidate));
              saveDraft(working, workingGroups, catalogSelection, true);
              failures++;
              continue;
            }
            if (!recovered) {
              group.needsReview = !group.cardId && isUnconfirmedPost(cause);
              group.error = group.needsReview
                ? `The create request could not be confirmed (${errorMessage(cause)}). The group may already exist. Check the gallery before retrying this POST.`
                : errorMessage(cause);
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, error: group.error, needsReview: group.needsReview } : candidate));
              saveDraft(working, workingGroups, catalogSelection, true);
              failures++;
              continue;
            }
          }
        }
        if (!mounted.current || pauseRequestedRef.current) { paused = true; break; }
        if (hasCatalogAssignment && group.cardId && group.metadataStatus !== 'assigned') {
          setCurrentStep(`Assigning tags/source to ${group.type} group ${group.title || group.id}`);
          setGroups((current) => current.map((candidate) => candidate.id === group.id
            ? { ...candidate, metadataStatus: 'assigning', metadataError: undefined } : candidate));
          try {
            for (const tag of plan.tags) {
              if (pauseRequestedRef.current) break;
              if (group.assignedTagIds?.includes(tag.id)) continue;
              await setCardTag(group.cardId, tag.id, signal);
              group.assignedTagIds = [...(group.assignedTagIds ?? []), tag.id];
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, assignedTagIds: group.assignedTagIds } : candidate));
              saveDraft(working, workingGroups, catalogSelection, true);
            }
            if (!pauseRequestedRef.current && plan.source && !group.sourceAssigned) {
              await setCardSource(group.cardId, plan.source.id, plan.originalUrl, signal);
              group.sourceAssigned = true;
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, sourceAssigned: true } : candidate));
              saveDraft(working, workingGroups, catalogSelection, true);
            }
            group.metadataStatus = pauseRequestedRef.current ? 'pending' : 'assigned';
            group.metadataError = undefined;
            setGroups((current) => current.map((candidate) => candidate.id === group.id
              ? { ...candidate, metadataStatus: group.metadataStatus, metadataError: undefined } : candidate));
          } catch (cause) {
            if (signal.aborted) {
              group.metadataStatus = 'pending';
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, metadataStatus: 'pending', metadataError: undefined } : candidate));
              paused = true;
              break;
            }
            group.metadataStatus = 'error';
            group.metadataError = errorMessage(cause);
            setGroups((current) => current.map((candidate) => candidate.id === group.id
              ? { ...candidate, metadataStatus: 'error', metadataError: group.metadataError } : candidate));
            failures++;
          }
        }
        if (!mounted.current || pauseRequestedRef.current) { paused = true; break; }
      }
      setNotice(cancelRequestedRef.current
        ? 'Import cancelled. Files whose upload was interrupted have an unknown outcome: Upload / retry checks their creation key before sending anything again. Files not yet started stay queued.'
        : paused || pauseRequestedRef.current
        ? 'Import paused. Use Upload / retry to continue from the remaining steps.'
        : failures ? `${failures} upload, metadata, or collection step(s) need attention. Retry after reviewing the errors.` : 'Import finished.');
    } finally {
      abortRef.current = null;
      setCurrentStep(null);
      setIsRunning(false);
      setPauseRequested(false);
      setCancelRequested(false);
    }
  }

  async function checkServerDuplicates(working: ImportItem[], signal: AbortSignal, stopRequested: () => boolean) {
    const eligible = working.filter((item) => !item.cardId && !item.creationKey && item.status !== 'needs-review'
      && !item.uploadAnyway && (item.file ? isSupported(item.file) : !!item.sha256));
    const toHash = eligible.filter((item) => item.file && !item.sha256 && !item.hashSkipped);
    if (toHash.length > 0) {
      let next = 0;
      let done = 0;
      await Promise.all(Array.from({ length: Math.min(hashConcurrency, toHash.length) }, async () => {
        while (next < toHash.length && !stopRequested()) {
          const item = toHash[next++];
          setCurrentStep(`Checking content hash ${++done}/${toHash.length}`);
          if (item.file!.size > maxHashBytes) {
            item.hashSkipped = true;
            updateItem(item.id, { hashSkipped: true });
            continue;
          }
          const sha256 = await tryFullHash(item.file!);
          if (sha256) {
            item.sha256 = sha256;
            updateItem(item.id, { sha256 });
          }
        }
      }));
    }
    if (stopRequested()) return;
    const hashed = eligible.filter((item) => item.sha256);
    // Same content twice in one batch: keep the first row, hold later ones until the user confirms.
    const firstByHash = new Map<string, string>();
    for (const item of working) {
      if (item.sha256 && (item.cardId || item.creationKey || item.uploadAnyway) && !firstByHash.has(item.sha256)) {
        firstByHash.set(item.sha256, item.descriptor.name);
      }
    }
    for (const item of hashed) {
      const first = firstByHash.get(item.sha256!);
      item.duplicateOfName = first;
      if (!first) firstByHash.set(item.sha256!, item.descriptor.name);
      updateItem(item.id, { duplicateOfName: item.duplicateOfName });
    }
    if (hashed.length > 0) {
      try {
        setCurrentStep('Checking for duplicates on the server');
        const matches = await findDuplicatesByHash([...new Set(hashed.map((item) => item.sha256!))], signal);
        const byHash = new Map(matches.map((match) => [match.sha256, match.cardIds]));
        let found = 0;
        for (const item of hashed) {
          const ids = byHash.get(item.sha256!);
          item.duplicateCardIds = ids && ids.length > 0 ? ids : undefined;
          if (item.duplicateCardIds) found++;
          updateItem(item.id, { duplicateCardIds: item.duplicateCardIds });
        }
        saveDraft(working, groupsRef.current, catalogSelection, true);
        if (found > 0) setNotice(`${found} file(s) already exist in the library and were not uploaded. Use "Upload anyway" on a row to import it regardless.`);
      } catch (cause) {
        if (!signal.aborted) setNotice(`Server duplicate check failed (${errorMessage(cause)}). Uploading continues without it.`);
      }
    }
  }

  function requestCancel() {
    cancelRequestedRef.current = true;
    setCancelRequested(true);
    pauseRequestedRef.current = true;
    setPauseRequested(true);
    abortRef.current?.abort();
  }

  function requestPause() {
    pauseRequestedRef.current = true;
    setPauseRequested(true);
  }

  function clearLocalDraft() {
    if (!window.confirm('Clear the saved local draft and all rows on this page? Files and group organization in this queue will be lost. Cards already created on the server will remain in your gallery.')) return;
    try {
      window.localStorage.removeItem(draftKey);
      fileSelectionGeneration.current++;
      draftEnabled.current = false;
      urls.current.forEach((url) => URL.revokeObjectURL(url));
      urls.current.clear();
      pendingItemPosts.current.clear();
      pendingGroupPosts.current.clear();
      catalogPlan.current = null;
      nextId.current = 1;
      setItems([]);
      setGroups([]);
      setCatalogSelection(emptySelection);
      setSelectionLocked(false);
      setPersistenceWarning(null);
      setNotice('Local draft and queue cleared. Cards already created on the server remain in your gallery.');
    } catch {
      setPersistenceWarning('Local draft could not be cleared because browser storage is unavailable.');
    }
  }

  const completedFiles = items.filter((item) => item.cardId).length;
  const analyzedFiles = items.filter((item) => item.cardId && item.analysis?.state === 'done').length;
  const analysisFailures = items.filter((item) => item.cardId && item.analysis?.state === 'failed').length;
  const failedFiles = items.filter((item) => !item.cardId && item.status === 'error').length;
  const reviewFiles = items.filter((item) => item.status === 'needs-review').length;
  const pendingFiles = items.length - completedFiles - failedFiles - reviewFiles;
  const duplicateFiles = items.filter(isBlockedDuplicate).length;
  const hasCatalogAssignment = catalogPlan.current
    ? catalogPlan.current.tags.length > 0 || catalogPlan.current.source !== null
    : catalogSelection.tags.length > 0 || catalogSelection.source !== null;

  const hasWork = items.some((item) => !item.cardId && item.status !== 'needs-review' && !isBlockedDuplicate(item)
      && (!!item.creationKey || !!item.file && isSupported(item.file)))
    || items.some((item) => item.cardId && (catalogPlan.current
      ? (catalogPlan.current.tags.length > 0 || catalogPlan.current.source !== null) && item.metadataStatus !== 'assigned'
      : catalogSelection.tags.length > 0 || catalogSelection.source !== null))
    || groups.some((group) => {
      const members = items.filter((item) => item.groupId === group.id);
      const childCardIds = members.flatMap((item) => item.cardId ? [item.cardId] : []);
      return members.length > 0 && !group.needsReview && (!group.cardId || group.error || group.needsSync
        || !hasSyncedChildren(group, childCardIds)
        || (hasCatalogAssignment && group.metadataStatus !== 'assigned'));
    });

  return (
    <section className="mx-auto flex w-full max-w-5xl flex-col gap-5 p-4 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h1 className="text-2xl font-semibold">Bulk import</h1>
        <details className="text-muted-foreground text-sm">
          <summary className="flex cursor-pointer items-center gap-1"><CircleHelp className="size-4" /> How it works</summary>
          <p className="mt-2 max-w-md">Files upload individually. Add comics or sets after upload. Queue details are saved in this browser; after a reload, reselect any missing files. Matching checks the name, size, modified time, type and a sample of the first and last 64 KiB, not the whole file.</p>
        </details>
      </div>

      <div onDragOver={(event) => { event.preventDefault(); setIsDragging(true); }}
        onDragLeave={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node)) setIsDragging(false); }}
        onDrop={(event) => { event.preventDefault(); setIsDragging(false); void addFiles(event.dataTransfer.files); }}
        className={`rounded-xl border-2 border-dashed p-6 text-center ${isDragging ? 'border-primary bg-accent' : 'border-border'}`}>
        <Upload className="text-muted-foreground mx-auto mb-2 size-6" aria-hidden="true" />
        <p className="mb-3 text-sm font-medium">Drop files here</p>
        <label className="focus-within:ring-ring inline-flex cursor-pointer items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium hover:bg-accent focus-within:ring-2">
          <Plus className="size-4" aria-hidden="true" /> Browse files
          <input className="sr-only" type="file" multiple accept=".jpg,.jpeg,.png,.webp,.gif,.mp4,.mov,.webm,image/jpeg,image/png,image/webp,image/gif,video/mp4,video/quicktime,video/webm"
            disabled={isRunning || isPreparingFiles} onChange={(event) => { if (event.target.files) void addFiles(event.target.files); event.target.value = ''; }} />
        </label>
      </div>

      <BulkImportCatalogPicker selection={catalogSelection} onChange={(selection) => { draftEnabled.current = true; setCatalogSelection(selection); }} locked={selectionLocked} />

      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="outline" disabled={isRunning} onClick={addGroup}><Plus className="size-4" /> Add group</Button>
        <Button type="button" disabled={isRunning || isPreparingFiles || !!reconciling || !hasWork} onClick={runImport}><Upload className="size-4" />{isRunning ? 'Importing...' : isPreparingFiles ? 'Checking files...' : 'Upload / retry'}</Button>
        <Button type="button" variant="ghost" size="icon" aria-label="Clear local draft" title="Clear local draft" disabled={isRunning || isPreparingFiles} onClick={clearLocalDraft}><Trash2 className="size-4" /></Button>
        {isRunning && <Button type="button" variant="outline" disabled={pauseRequested} onClick={requestPause}>
          <Pause className="size-4" />{pauseRequested ? 'Pausing...' : 'Pause'}
        </Button>}
        {isRunning && <Button type="button" variant="outline" aria-label="Cancel import" disabled={cancelRequested} onClick={requestCancel}>
          <X className="size-4" />{cancelRequested ? 'Cancelling...' : 'Cancel'}
        </Button>}
        <span className="text-muted-foreground text-sm" aria-label={`${items.length} total, ${completedFiles} uploaded, ${analyzedFiles} analyzed, ${failedFiles} upload errors, ${analysisFailures} analysis errors, ${reviewFiles} need review, ${pendingFiles} pending`}>
          {completedFiles}/{items.length} uploaded{completedFiles > 0 && ` · ${analyzedFiles}/${completedFiles} analyzed`}
          {failedFiles > 0 && ` · ${failedFiles} upload errors`}{analysisFailures > 0 && ` · ${analysisFailures} analysis errors`}
          {reviewFiles > 0 && ` · ${reviewFiles} review`}{duplicateFiles > 0 && ` · ${duplicateFiles} duplicate`}
        </span>
      </div>
      {isRunning && <p role="status" className="text-muted-foreground text-sm">Current: {currentStep ?? 'Preparing next step'}{pauseRequested ? ' · pause requested' : ''}</p>}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {persistenceWarning && <p role="alert" className="text-destructive text-sm">{persistenceWarning}</p>}

      {groups.length > 0 && <div className="grid gap-3 sm:grid-cols-2">
        {groups.map((group) => <div key={group.id} className="rounded-xl border p-4">
          <div className="mb-3 flex items-center gap-2">
            <select aria-label={`Group ${group.id} type`} className="border-input bg-background h-9 rounded-lg border px-2" value={group.type}
              disabled={isRunning || !!group.cardId || !!group.creationKey} onChange={(event) => setGroups((current) => current.map((candidate) => candidate.id === group.id ? { ...candidate, type: event.target.value as 'comic' | 'set' } : candidate))}>
              <option value="comic">Comic</option><option value="set">Set</option>
            </select>
            <Input aria-label={`Group ${group.id} title`} placeholder="Group title" value={group.title} disabled={isRunning || !!group.cardId || !!group.creationKey}
              onChange={(event) => setGroups((current) => current.map((candidate) => candidate.id === group.id ? { ...candidate, title: event.target.value } : candidate))} />
          </div>
          <p className="text-muted-foreground text-xs">{items.filter((item) => item.groupId === group.id).length} files · order follows the queue below</p>
          {group.cardId && <a className="text-primary text-sm underline" href={`/cards/${group.cardId}`}>Open {group.type} #{group.cardId}</a>}
          {group.needsSync && <p className="text-muted-foreground text-sm">Order changed. Run Upload / retry to save it.</p>}
          {group.error && <p role="alert" className="text-destructive text-sm">{group.error}</p>}
          {group.needsReview && <div className="flex flex-wrap items-center gap-2 text-sm">
            <a className="text-primary underline" href="/gallery">Check gallery</a>
            <Input aria-label={`Existing card ID for group ${group.id}`} className="w-40" inputMode="numeric" placeholder="Existing group ID"
              value={groupReviewIds[group.id] ?? ''} disabled={isRunning || !!reconciling}
              onChange={(event) => setGroupReviewIds((current) => ({ ...current, [group.id]: event.target.value }))} />
            <Button type="button" size="sm" variant="outline" disabled={isRunning || !!reconciling}
              onClick={() => void reconcileGroup(group.id)}>{reconciling === `group-${group.id}` ? 'Checking...' : 'Verify existing group'}</Button>
            <Button type="button" size="sm" variant="outline" disabled={isRunning || !!reconciling} onClick={() =>
              setGroups((current) => current.map((candidate) => candidate.id === group.id
                ? { ...candidate, needsReview: false, error: undefined } : candidate))}>
              Retry group POST anyway
            </Button>
            <span className="text-muted-foreground">This may create a duplicate group. Then choose Upload / retry.</span>
            {reviewErrors[`group-${group.id}`] && <p role="alert" className="w-full text-destructive">{reviewErrors[`group-${group.id}`]}</p>}
          </div>}
          {group.cardId && hasCatalogAssignment && <p className="text-muted-foreground text-xs">Tags/source: {group.metadataStatus ?? 'pending'}</p>}
          {group.metadataError && <p role="alert" className="text-destructive text-sm">Could not assign tags/source: {group.metadataError}</p>}
          {!group.cardId && !group.creationKey && !isRunning && <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove ${group.type} group ${group.title || group.id}`} title="Remove group" onClick={() => {
            setItems((current) => current.map((item) => item.groupId === group.id ? { ...item, groupId: null } : item));
            setGroups((current) => current.filter((candidate) => candidate.id !== group.id));
          }}><Trash2 className="size-4" /></Button>}
        </div>)}
      </div>}

      <div className="grid gap-3">
        {items.length === 0 && <p className="text-muted-foreground text-sm">No files selected.</p>}
        {items.map((item) => {
          const group = groups.find((candidate) => candidate.id === item.groupId);
          const groupItems = items.filter((candidate) => candidate.groupId === item.groupId);
          const position = groupItems.findIndex((candidate) => candidate.id === item.id);
          const locked = !!group?.cardId && !!item.cardId;
          return <article key={item.id} className="flex flex-col gap-3 rounded-xl border p-3 sm:flex-row sm:items-center [content-visibility:auto] [contain-intrinsic-size:auto_120px]">
            <div className="bg-muted flex size-20 shrink-0 items-center justify-center overflow-hidden rounded-lg">
              {!item.previewUrl ? <span className="text-muted-foreground text-xs">No file</span>
                : item.descriptor.type.startsWith('video/') || /\.(mp4|mov|webm)$/i.test(item.descriptor.name)
                  ? <video className="size-full object-cover" src={item.previewUrl} muted playsInline preload="none" />
                  : <img className="size-full object-cover" src={item.previewUrl} alt="" loading="lazy" decoding="async" />}
            </div>
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium" title={item.descriptor.name}>{item.descriptor.name}</p>
              <Input aria-label={`Title for ${item.descriptor.name}`} value={item.title} placeholder="Card title"
                disabled={isRunning || !!item.cardId || !!item.creationKey}
                onChange={(event) => updateItem(item.id, { title: event.target.value })} />
              <p className="text-muted-foreground flex flex-wrap items-center gap-2 text-xs">
                <Badge variant="outline">{mediaTypeLabel(item.descriptor)}</Badge>
                <span>{(item.descriptor.size / 1024 / 1024).toFixed(1)} MB · {item.status}</span>
              </p>
              {item.hashSkipped && !item.sha256 && <p className="text-muted-foreground text-xs">File is larger than 1 GiB; it was not checked for server duplicates.</p>}
              {!!item.duplicateCardIds?.length && !item.cardId && <div className="flex flex-wrap items-center gap-2 text-sm">
                <span role="status">Duplicate of {item.duplicateCardIds.map((id, index) => <React.Fragment key={id}>{index > 0 && ', '}<a className="text-primary underline" href={`/cards/${id}`}>#{id}</a></React.Fragment>)}</span>
                {!item.uploadAnyway && <Button type="button" size="sm" variant="outline" disabled={isRunning} aria-label={`Upload ${item.descriptor.name} anyway`}
                  onClick={() => updateItem(item.id, { uploadAnyway: true })}>Upload anyway</Button>}
                {item.uploadAnyway && <span className="text-muted-foreground">Will upload anyway.</span>}
              </div>}
              {!!item.duplicateOfName && !item.duplicateCardIds?.length && !item.cardId && <div className="flex flex-wrap items-center gap-2 text-sm">
                <span role="status">Same content as {item.duplicateOfName} in this batch</span>
                {!item.uploadAnyway && <Button type="button" size="sm" variant="outline" disabled={isRunning} aria-label={`Upload ${item.descriptor.name} anyway`}
                  onClick={() => updateItem(item.id, { uploadAnyway: true })}>Upload anyway</Button>}
                {item.uploadAnyway && <span className="text-muted-foreground">Will upload anyway.</span>}
              </div>}
              {!item.file && !item.cardId && <div className="mt-2 text-sm">
                <p role="status">File missing after reload. Reselect it here. {!item.fingerprint && 'Check that this is the intended file before upload.'}</p>
                <label className="focus-within:ring-ring mt-1 inline-flex cursor-pointer items-center gap-1 rounded-lg border px-3 py-1 focus-within:ring-2">
                  <Upload className="size-4" /> Attach file
                  <input className="sr-only" type="file" disabled={isRunning || isPreparingFiles} aria-label={`Attach file for ${item.descriptor.name}`}
                    onChange={(event) => { const file = event.target.files?.[0]; if (file) void attachFile(item, file); event.target.value = ''; }} />
                </label>
              </div>}
              {item.fingerprintUnavailable && <p role="alert" className="text-destructive text-xs">Sampled fingerprint could not be calculated. This file can upload now; after reload, attach it manually and check that it is the intended file.</p>}
              {item.possibleDuplicate && <p className="text-muted-foreground text-xs">Possible duplicate selection: same name, size, and modified time as another queued file. Review it before uploading.</p>}
              {item.error && <p role="alert" className="text-destructive text-sm">{item.error}</p>}
              {item.status === 'needs-review' && <div className="flex flex-wrap items-center gap-2 text-sm">
                <a className="text-primary underline" href="/gallery">Check gallery</a>
                <Input aria-label={`Existing card ID for ${item.descriptor.name}`} className="w-40" inputMode="numeric" placeholder="Existing media ID"
                  value={mediaReviewIds[item.id] ?? ''} disabled={isRunning || !!reconciling}
                  onChange={(event) => setMediaReviewIds((current) => ({ ...current, [item.id]: event.target.value }))} />
                <Button type="button" size="sm" variant="outline" disabled={isRunning || !!reconciling}
                  onClick={() => void reconcileMedia(item.id)}>{reconciling === `media-${item.id}` ? 'Checking...' : 'Verify existing media'}</Button>
                <Button type="button" size="sm" variant="outline" disabled={isRunning || !!reconciling} onClick={() => updateItem(item.id, { status: 'queued', error: undefined })}>
                  Retry media POST anyway
                </Button>
                <span className="text-muted-foreground">This may create a duplicate card. Then choose Upload / retry.</span>
                {reviewErrors[`media-${item.id}`] && <p role="alert" className="w-full text-destructive">{reviewErrors[`media-${item.id}`]}</p>}
              </div>}
              {item.cardId && <>
                <a className="text-primary text-sm underline" href={`/cards/${item.cardId}`}>Open media #{item.cardId}</a>
                {hasCatalogAssignment && <p className="text-muted-foreground text-xs">Tags/source: {item.metadataStatus ?? 'pending'}</p>}
                {item.metadataError && <p role="alert" className="text-destructive text-sm">Could not assign tags/source: {item.metadataError}</p>}
                <details className="text-muted-foreground text-xs"><summary className="cursor-pointer">Analysis: {item.analysis === null ? 'not queued' : item.analysis?.state ?? 'checking'}</summary>
                  {item.analysis && <p>{item.analysis.attempts} attempt(s){item.analysis.availableAtUtc && item.analysis.state === 'pending' && ` · next attempt ${new Date(item.analysis.availableAtUtc).toLocaleString()}`}</p>}
                </details>
                {item.analysis?.lastError && <p className="text-destructive text-xs">Analysis error: {item.analysis.lastError}</p>}
                {item.analysisError && <p role="alert" className="text-destructive text-xs">Could not check analysis: {item.analysisError}</p>}
                <Button type="button" size="icon-sm" variant="ghost" aria-label={`Refresh analysis for ${item.descriptor.name}`} title="Refresh analysis" onClick={() => void checkAnalysis(item)}><RotateCw className="size-4" /></Button>
              </>}
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <select aria-label={`Assign ${item.descriptor.name}`} value={item.groupId ?? ''} disabled={isRunning || locked || !!group?.creationKey && !group.cardId}
                className="border-input bg-background h-9 rounded-lg border px-2 text-sm"
                onChange={(event) => updateItem(item.id, { groupId: event.target.value ? Number(event.target.value) : null })}>
                <option value="">Standalone</option>
                {groups.filter((candidate) => (!candidate.cardId && !candidate.creationKey) || candidate.id === item.groupId).map((candidate) =>
                  <option key={candidate.id} value={candidate.id}>{candidate.title || `${candidate.type} group ${candidate.id}`}</option>)}
              </select>
              {item.groupId != null && <>
                <Button type="button" size="icon-sm" variant="outline" aria-label={`Move ${item.descriptor.name} up`} title="Move up" disabled={isRunning || !!group?.creationKey && !group.cardId || position === 0} onClick={() => moveItem(item.id, -1)}><ArrowUp className="size-4" /></Button>
                <Button type="button" size="icon-sm" variant="outline" aria-label={`Move ${item.descriptor.name} down`} title="Move down" disabled={isRunning || !!group?.creationKey && !group.cardId || position === groupItems.length - 1} onClick={() => moveItem(item.id, 1)}><ArrowDown className="size-4" /></Button>
              </>}
              {!item.cardId && <Button type="button" size="icon-sm" variant="ghost" aria-label={`Remove ${item.descriptor.name}`} title="Remove file" disabled={isRunning || !!item.creationKey || !!group?.creationKey && !group.cardId} onClick={() => removeItem(item)}><Trash2 className="size-4" /></Button>}
            </div>
          </article>;
        })}
      </div>
    </section>
  );
}
