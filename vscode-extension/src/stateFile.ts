import * as fs from 'fs';
import * as path from 'path';

// Each VS Code window owns exactly one file named after its session id, so concurrent
// windows never write the same file. Writes go to a temporary file in the same directory
// and are renamed into place, so readers never observe a partially written report.

export const SESSION_SUFFIX = '.session.json';

export function defaultStateDirectory(env: NodeJS.ProcessEnv = process.env): string | null {
  if (env.TASKBAR_ICON_OVERLAY_STATE_DIR) return env.TASKBAR_ICON_OVERLAY_STATE_DIR;
  if (!env.LOCALAPPDATA) return null;
  return path.join(env.LOCALAPPDATA, 'TaskbarIconOverlay', 'sessions');
}

export function sessionFilePath(directory: string, sessionId: string): string {
  return path.join(directory, sessionId + SESSION_SUFFIX);
}

export function writeAtomic(file: string, contents: string): void {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(temp, contents, 'utf8');
  try {
    renameWithRetry(temp, file);
  } catch (err) {
    tryDelete(temp);
    throw err;
  }
}

// On Windows a rename over a file that a reader has open (without FILE_SHARE_DELETE)
// fails with EPERM/EBUSY. TaskbarIconOverlay opens with FILE_SHARE_DELETE, but antivirus
// and indexers may not, so retry briefly.
function renameWithRetry(from: string, to: string): void {
  for (let attempt = 0; ; attempt++) {
    try {
      fs.renameSync(from, to);
      return;
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (attempt >= 5 || (code !== 'EPERM' && code !== 'EBUSY' && code !== 'EACCES')) throw err;
      const until = Date.now() + 20 * (attempt + 1);
      while (Date.now() < until) {
        // Short synchronous back-off; total worst case is ~300ms.
      }
    }
  }
}

export function tryDelete(file: string): void {
  try {
    fs.unlinkSync(file);
  } catch {
    // Already gone or locked; TaskbarIconOverlay removes stale files too.
  }
}

/** Process liveness check that does not signal the process. */
export function isProcessAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    // EPERM means the process exists but belongs to someone else.
    return (err as NodeJS.ErrnoException).code === 'EPERM';
  }
}

/**
 * Removes session files left behind by windows whose extension host died without
 * deactivating (crash, kill), plus orphaned temporary files older than a minute.
 */
export function removeStaleSessions(
  directory: string,
  isAlive: (pid: number) => boolean = isProcessAlive,
  nowMs: number = Date.now(),
): string[] {
  let entries: string[];
  try {
    entries = fs.readdirSync(directory);
  } catch {
    return [];
  }
  const removed: string[] = [];
  for (const name of entries) {
    const file = path.join(directory, name);
    if (name.endsWith('.tmp')) {
      try {
        if (nowMs - fs.statSync(file).mtimeMs > 60_000) {
          tryDelete(file);
          removed.push(name);
        }
      } catch {
        // ignore
      }
      continue;
    }
    if (!name.endsWith(SESSION_SUFFIX)) continue;
    let pid = NaN;
    try {
      pid = JSON.parse(fs.readFileSync(file, 'utf8')).pid;
    } catch {
      // Unreadable or corrupt: fall through and treat as stale only if old.
      try {
        if (nowMs - fs.statSync(file).mtimeMs < 60_000) continue;
      } catch {
        continue;
      }
    }
    if (!isAlive(pid)) {
      tryDelete(file);
      removed.push(name);
    }
  }
  return removed;
}
