import type { HubApi } from '../preload/index';

declare global {
  interface Window {
    /** The preload bridge. Injected by `contextBridge.exposeInMainWorld`. */
    hub: HubApi;
  }
}

export {};
