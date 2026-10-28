import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import {
  buildReport,
  remoteHostFromAuthority,
  reportChanged,
  ReportInputs,
  titleNameMode,
  WorkspaceInfo,
  workspaceId,
} from '../report';

function ws(overrides: Partial<WorkspaceInfo>): WorkspaceInfo {
  return { name: 'proj', workspaceFile: undefined, folders: [], remoteName: undefined, remoteAuthority: undefined, ...overrides };
}

function inputs(workspace: WorkspaceInfo): ReportInputs {
  return {
    sessionId: 's1',
    pid: 10,
    ppid: 1,
    processStartMs: 1000,
    appName: 'Visual Studio Code',
    workspace,
    titleTemplate: undefined,
    titleSeparator: undefined,
    resolved: { color: '#ff0000', source: 'peacock.color' },
    focused: true,
    focusedAtMs: 5,
    nowMs: 100,
  };
}

test('same folder name on different SSH hosts gets different identities', () => {
  const a = ws({ folders: ['vscode-remote://ssh-remote+alpha/home/me/proj'], remoteName: 'ssh-remote', remoteAuthority: 'ssh-remote+alpha' });
  const b = ws({ folders: ['vscode-remote://ssh-remote+beta/home/me/proj'], remoteName: 'ssh-remote', remoteAuthority: 'ssh-remote+beta' });
  assert.notEqual(workspaceId(a), workspaceId(b));
  assert.equal(buildReport(inputs(a)).remoteHost, 'alpha');
  assert.equal(buildReport(inputs(b)).remoteHost, 'beta');
});

test('same folder name in different paths on the same host gets different identities', () => {
  const a = ws({ folders: ['vscode-remote://ssh-remote+alpha/a/proj'] });
  const b = ws({ folders: ['vscode-remote://ssh-remote+alpha/b/proj'] });
  assert.notEqual(workspaceId(a), workspaceId(b));
});

test('workspace identity is stable and does not reveal paths', () => {
  const a = ws({ folders: ['file:///c%3A/secret/proj'] });
  const id = workspaceId(a)!;
  assert.equal(id, workspaceId(ws({ folders: ['file:///c%3A/secret/proj'] })));
  assert.match(id, /^[0-9a-f]{16}$/);
  assert.ok(!JSON.stringify(buildReport(inputs(a))).includes('secret'));
});

test('multi-root workspace identity uses the workspace file; folder order does not matter otherwise', () => {
  const file = ws({ workspaceFile: 'vscode-remote://ssh-remote+h/x.code-workspace', folders: ['a', 'b'] });
  assert.equal(buildReport(inputs(file)).workspaceKind, 'multi-root');
  assert.equal(workspaceId(ws({ folders: ['a', 'b'] })), workspaceId(ws({ folders: ['b', 'a'] })));
});

test('decodes Remote SSH authorities', () => {
  assert.equal(remoteHostFromAuthority('ssh-remote+myhost'), 'myhost');
  assert.equal(remoteHostFromAuthority('ssh-remote+user%40box.example.com'), 'user@box.example.com');
  const hex = Buffer.from(JSON.stringify({ hostName: 'build-01' })).toString('hex');
  assert.equal(remoteHostFromAuthority(`ssh-remote+${hex}`), 'build-01');
  assert.equal(remoteHostFromAuthority('ssh-remote+cafe'), 'cafe');
  assert.equal(remoteHostFromAuthority(undefined), null);
  assert.equal(remoteHostFromAuthority('wsl'), null);
});

test('title name mode follows the window.title template', () => {
  assert.equal(titleNameMode(undefined), 'rootName');
  assert.equal(titleNameMode('${dirty}${activeEditorShort}${separator}${rootName}'), 'rootName');
  assert.equal(titleNameMode('${rootNameShort} - ${appName}'), 'rootNameShort');
  assert.equal(titleNameMode('${activeEditorLong}'), 'none');
});

test('empty window reports no colour', () => {
  const r = buildReport(inputs(ws({ name: undefined })));
  assert.equal(r.workspaceKind, 'none');
  assert.equal(r.color, null);
  assert.equal(r.workspaceName, null);
});

test('reportChanged ignores the write timestamp only', () => {
  const a = buildReport(inputs(ws({ folders: ['x'] })));
  assert.equal(reportChanged(a, { ...a, updatedAtMs: 999 }), false);
  assert.equal(reportChanged(a, { ...a, color: '#00ff00' }), true);
  assert.equal(reportChanged(a, { ...a, focusedAtMs: 6 }), true);
  assert.equal(reportChanged(undefined, a), true);
});

test('report keys match the shared fixture read by TaskbarIconOverlay (PeacockSession.cs)', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const fixture = JSON.parse(require('fs').readFileSync(require('path').join(__dirname, '../../../tests/fixtures/session-v1.json'), 'utf8'));
  const report = buildReport(inputs(ws({ folders: ['vscode-remote://ssh-remote+build-01/srv/api'] })));
  assert.deepEqual(Object.keys(report).sort(), Object.keys(fixture).sort());
  assert.equal(report.schemaVersion, fixture.schemaVersion);
});
