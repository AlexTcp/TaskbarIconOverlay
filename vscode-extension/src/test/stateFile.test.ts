import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { removeStaleSessions, sessionFilePath, writeAtomic } from '../stateFile';

function tempDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'tio-'));
}

test('writeAtomic replaces content and leaves no temp files', () => {
  const dir = tempDir();
  const file = sessionFilePath(dir, 'a');
  writeAtomic(file, '{"v":1}');
  writeAtomic(file, '{"v":2}');
  assert.equal(fs.readFileSync(file, 'utf8'), '{"v":2}');
  assert.deepEqual(fs.readdirSync(dir), ['a.session.json']);
});

test('writeAtomic never leaves a partial file when the target is locked', () => {
  // Windows refuses to rename over a file that another handle has open. The writer retries
  // briefly, then gives up without corrupting the existing report or leaking temp files;
  // the extension schedules another attempt.
  const dir = tempDir();
  const file = sessionFilePath(dir, 'a');
  writeAtomic(file, '1');
  const fd = fs.openSync(file, 'r');
  try {
    try {
      writeAtomic(file, '2');
    } catch (err) {
      assert.match((err as NodeJS.ErrnoException).code ?? '', /EPERM|EBUSY|EACCES/);
    }
  } finally {
    fs.closeSync(fd);
  }
  assert.ok(['1', '2'].includes(fs.readFileSync(file, 'utf8')));
  assert.deepEqual(fs.readdirSync(dir), ['a.session.json']);
});

test('separate windows write separate files', () => {
  const dir = tempDir();
  writeAtomic(sessionFilePath(dir, 'one'), '{"pid":1}');
  writeAtomic(sessionFilePath(dir, 'two'), '{"pid":2}');
  assert.deepEqual(fs.readdirSync(dir).sort(), ['one.session.json', 'two.session.json']);
});

test('removeStaleSessions removes files of dead processes only', () => {
  const dir = tempDir();
  writeAtomic(sessionFilePath(dir, 'alive'), JSON.stringify({ pid: 100 }));
  writeAtomic(sessionFilePath(dir, 'dead'), JSON.stringify({ pid: 200 }));
  fs.writeFileSync(path.join(dir, 'unrelated.txt'), 'x');
  const removed = removeStaleSessions(dir, pid => pid === 100);
  assert.deepEqual(removed, ['dead.session.json']);
  assert.deepEqual(fs.readdirSync(dir).sort(), ['alive.session.json', 'unrelated.txt']);
});

test('removeStaleSessions keeps fresh corrupt files but removes old temp files', () => {
  const dir = tempDir();
  fs.writeFileSync(sessionFilePath(dir, 'corrupt'), '{not json');
  const tmp = path.join(dir, 'x.session.json.1.2.tmp');
  fs.writeFileSync(tmp, '');
  const old = new Date(Date.now() - 120_000);
  fs.utimesSync(tmp, old, old);
  const removed = removeStaleSessions(dir, () => false);
  assert.deepEqual(removed, ['x.session.json.1.2.tmp']);
  assert.ok(fs.existsSync(sessionFilePath(dir, 'corrupt')));
});

test('removeStaleSessions tolerates a missing directory', () => {
  assert.deepEqual(removeStaleSessions(path.join(tempDir(), 'missing')), []);
});
