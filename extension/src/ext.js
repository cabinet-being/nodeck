// One namespace for Chrome and Firefox: both expose promise-based APIs in Manifest V3.
export const ext = globalThis.browser ?? globalThis.chrome;
