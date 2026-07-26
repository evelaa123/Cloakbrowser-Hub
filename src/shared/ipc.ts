/**
 * IPC channel names, shared by the main process, the preload bridge and the
 * renderer so a typo can never silently create a dead channel.
 */

export const IPC = {
  // Profiles
  PROFILES_LIST: 'profiles:list',
  PROFILES_GET: 'profiles:get',
  PROFILES_CREATE: 'profiles:create',
  PROFILES_UPDATE: 'profiles:update',
  PROFILES_DELETE: 'profiles:delete',
  PROFILES_DUPLICATE: 'profiles:duplicate',
  PROFILES_EXPORT: 'profiles:export',
  PROFILES_IMPORT: 'profiles:import',
  PROFILES_RANDOMIZE_FP: 'profiles:randomizeFingerprint',
  PROFILES_OPEN_DIR: 'profiles:openDir',
  PROFILES_PREVIEW_ARGS: 'profiles:previewArgs',

  // Sessions
  SESSION_START: 'session:start',
  SESSION_STOP: 'session:stop',
  SESSION_STOP_ALL: 'session:stopAll',
  SESSION_LIST: 'session:list',
  SESSION_LOGS: 'session:logs',

  // Cookies
  COOKIES_PICK_FILES: 'cookies:pickFiles',
  COOKIES_VALIDATE_FILE: 'cookies:validateFile',
  COOKIES_VALIDATE_TEXT: 'cookies:validateText',
  COOKIES_IMPORT_FILES: 'cookies:importFiles',
  COOKIES_IMPORT_TEXT: 'cookies:importText',
  COOKIES_EXPORT: 'cookies:export',
  COOKIES_CLEAR: 'cookies:clear',
  COOKIES_SUMMARY: 'cookies:summary',

  // Proxies
  PROXY_LIST: 'proxy:list',
  PROXY_ADD: 'proxy:add',
  PROXY_ADD_BULK: 'proxy:addBulk',
  PROXY_UPDATE: 'proxy:update',
  PROXY_DELETE: 'proxy:delete',
  PROXY_CHECK: 'proxy:check',
  PROXY_CHECK_SAVED: 'proxy:checkSaved',
  PROXY_PARSE: 'proxy:parse',
  PROXY_ROTATE: 'proxy:rotate',

  // License / binary
  LICENSE_STATE: 'license:state',
  LICENSE_ACTIVATE: 'license:activate',
  LICENSE_SIGN_IN_GITHUB: 'license:signInGithub',
  LICENSE_LOGOUT: 'license:logout',
  LICENSE_OPEN_PRICING: 'license:openPricing',
  BINARY_STATE: 'binary:state',
  BINARY_DOWNLOAD: 'binary:download',

  // Import
  IMPORT_DISCOVER: 'import:discover',
  IMPORT_BROWSER_PROFILE: 'import:browserProfile',
  /** Pick a folder and scan it for profiles (backups, external drives, copies). */
  IMPORT_SCAN_FOLDER: 'import:scanFolder',
  /** Pick a .zip, unpack it to temp and scan the result. */
  IMPORT_SCAN_ARCHIVE: 'import:scanArchive',
  /** Delete a temp extraction directory once the user is done with it. */
  IMPORT_CLEANUP: 'import:cleanup',

  // Automation API
  AUTOMATION_STATE: 'automation:state',
  AUTOMATION_SET: 'automation:set',
  AUTOMATION_ROTATE_TOKEN: 'automation:rotateToken',
  AUTOMATION_ENDPOINT: 'automation:endpoint',

  // Settings / app
  SETTINGS_GET: 'settings:get',
  SETTINGS_UPDATE: 'settings:update',
  APP_INFO: 'app:info',
  APP_PICK_DIR: 'app:pickDir',
  APP_OPEN_EXTERNAL: 'app:openExternal',
  APP_OPEN_PATH: 'app:openPath',

  // Events (main → renderer)
  EVT_SESSIONS: 'evt:sessions',
  EVT_LOG: 'evt:log',
  EVT_PROFILES_CHANGED: 'evt:profilesChanged',
  EVT_BINARY_PROGRESS: 'evt:binaryProgress',
} as const;

export type IpcChannel = (typeof IPC)[keyof typeof IPC];
