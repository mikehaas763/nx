/**
 * Tests for the analyzer's caching layer.
 *
 * The behavior under test is easy to get wrong and invisible when it breaks:
 * the analyzer's cache is keyed on the hash of project files, but atomized test
 * targets are derived from the *C# sources*. Without a second level of
 * invalidation, adding a test class leaves the cached analysis in place and the
 * new test silently never gets a target.
 *
 * The counterpart assertion matters just as much: a workspace where nothing
 * atomizes must not start hashing sources, because that would mean re-running an
 * MSBuild evaluation on every edit to any .cs file.
 */

const hashWithWorkspaceContext = jest.fn<Promise<string>, [string, string[]]>();
const spawnSync = jest.fn();
let pluginCacheEntries: Record<string, unknown> = {};

jest.mock('nx/src/utils/workspace-context', () => ({
  hashWithWorkspaceContext: (root: string, globs: string[]) =>
    hashWithWorkspaceContext(root, globs),
}));

jest.mock('node:child_process', () => ({
  ...jest.requireActual('node:child_process'),
  spawnSync: (...args: unknown[]) => spawnSync(...args),
}));

jest.mock('nx/src/utils/plugin-cache-utils', () => ({
  PluginCache: class {
    get(key: string) {
      return pluginCacheEntries[key];
    }
    set(key: string, value: unknown) {
      pluginCacheEntries[key] = value;
    }
    has(key: string) {
      return key in pluginCacheEntries;
    }
    writeToDisk() {}
  },
}));

// Keep the real exports: @nx/devkit pulls this module in transitively and
// needs `cacheDir` to be a real path at import time.
jest.mock('nx/src/utils/cache-directory', () => ({
  ...jest.requireActual('nx/src/utils/cache-directory'),
  workspaceDataDirectory: '/tmp/nx-dotnet-spec',
}));

jest.mock('@nx/devkit', () => ({
  ...jest.requireActual('@nx/devkit'),
  workspaceRoot: '/ws',
  logger: { error: jest.fn(), warn: jest.fn(), info: jest.fn() },
}));

import { analyzeProjects, clearCache } from './analyzer-client';

const PROJECT_FILES = ['apps/it/it.csproj'];

/** Makes the spawned analyzer return `payload` as its stdout. */
function analyzerReturns(payload: Record<string, unknown>) {
  spawnSync.mockReturnValue({
    status: 0,
    stdout: JSON.stringify(payload),
    stderr: '',
  });
}

const EMPTY_ANALYSIS = { nodesByFile: {}, referencesByRoot: {} };
const ATOMIZED_ANALYSIS = {
  ...EMPTY_ANALYSIS,
  atomizedRoots: ['apps/it'],
};

/** Glob groups passed to the hasher, in call order. */
const hashedGlobs = () => hashWithWorkspaceContext.mock.calls.map(([, g]) => g);

describe('analyzer-client caching', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    pluginCacheEntries = {};
    clearCache();
    // Default: every hash request is distinct-but-stable per glob group.
    hashWithWorkspaceContext.mockImplementation(async (_root, globs) =>
      globs.join('|')
    );
    // The analyzer binary must exist for getAnalyzerPath(); bypass by making
    // spawnSync the only thing that runs.
    jest.spyOn(require('node:fs'), 'existsSync').mockReturnValue(true);
  });

  afterEach(() => jest.restoreAllMocks());

  describe('when nothing atomizes', () => {
    it('hashes only the project files, never the sources', async () => {
      analyzerReturns(EMPTY_ANALYSIS);

      await analyzeProjects(PROJECT_FILES);

      // Exactly one hash call, for the project files. This is the literal
      // encoding of "workspaces that do not opt in pay nothing".
      expect(hashedGlobs()).toEqual([PROJECT_FILES]);
    });

    it('serves the cached result without re-hashing sources', async () => {
      analyzerReturns(EMPTY_ANALYSIS);
      await analyzeProjects(PROJECT_FILES);
      const callsAfterFirstRun = hashWithWorkspaceContext.mock.calls.length;

      clearCache(); // force the on-disk cache path rather than the in-memory one
      await analyzeProjects(PROJECT_FILES);

      // One more call for the project-files hash, and nothing else.
      expect(hashWithWorkspaceContext.mock.calls.length).toBe(
        callsAfterFirstRun + 1
      );
      expect(spawnSync).toHaveBeenCalledTimes(1);
    });
  });

  describe('when a project atomizes', () => {
    it('additionally hashes that project root’s C# sources', async () => {
      analyzerReturns(ATOMIZED_ANALYSIS);

      await analyzeProjects(PROJECT_FILES);

      expect(hashedGlobs()).toEqual([PROJECT_FILES, ['apps/it/**/*.cs']]);
    });

    it('puts every atomized root in a single glob group', async () => {
      // Grouping per root would make the native hasher walk the full file list
      // once per project instead of once in total.
      analyzerReturns({
        ...EMPTY_ANALYSIS,
        atomizedRoots: ['apps/it', 'apps/e2e'],
      });

      await analyzeProjects(PROJECT_FILES);

      expect(hashedGlobs()[1]).toEqual(['apps/it/**/*.cs', 'apps/e2e/**/*.cs']);
    });

    it('reuses the cached result while sources are unchanged', async () => {
      analyzerReturns(ATOMIZED_ANALYSIS);
      await analyzeProjects(PROJECT_FILES);

      clearCache();
      await analyzeProjects(PROJECT_FILES);

      expect(spawnSync).toHaveBeenCalledTimes(1);
    });

    it('re-runs when a source changes even though project files did not', async () => {
      // The regression this whole mechanism exists for: adding a test class
      // changes no .csproj, so the project-files hash is identical.
      analyzerReturns(ATOMIZED_ANALYSIS);
      await analyzeProjects(PROJECT_FILES);
      expect(spawnSync).toHaveBeenCalledTimes(1);

      clearCache();
      hashWithWorkspaceContext.mockImplementation(async (_root, globs) =>
        globs[0].endsWith('.cs') ? 'sources-changed' : globs.join('|')
      );

      await analyzeProjects(PROJECT_FILES);

      expect(spawnSync).toHaveBeenCalledTimes(2);
    });

    it('does not re-run when an unrelated project’s sources change', async () => {
      analyzerReturns(ATOMIZED_ANALYSIS);
      await analyzeProjects(PROJECT_FILES);

      clearCache();
      // Hash of apps/it sources is unchanged; anything outside it is not part
      // of the glob group at all, so it cannot affect the result.
      await analyzeProjects(PROJECT_FILES);

      expect(spawnSync).toHaveBeenCalledTimes(1);
    });
  });

  describe('cache entry compatibility', () => {
    it('ignores entries written in the pre-atomizer shape', async () => {
      // An old entry is the bare analysis, not { result, sourceHash }. Reading
      // `.result` off it yields undefined, which must be treated as a miss
      // rather than propagated as a broken analysis.
      pluginCacheEntries[PROJECT_FILES.join('|')] = EMPTY_ANALYSIS;
      analyzerReturns(EMPTY_ANALYSIS);

      const result = await analyzeProjects(PROJECT_FILES);

      expect(spawnSync).toHaveBeenCalledTimes(1);
      expect(result).toEqual(EMPTY_ANALYSIS);
    });

    it('treats a result with no atomizedRoots field as non-atomizing', async () => {
      // Output from an analyzer that predates the field.
      analyzerReturns(EMPTY_ANALYSIS);

      await analyzeProjects(PROJECT_FILES);

      expect(hashedGlobs()).toEqual([PROJECT_FILES]);
    });
  });

  describe('errors', () => {
    it('does not write failures to the on-disk cache', async () => {
      spawnSync.mockReturnValue({ status: 1, stdout: '', stderr: 'boom' });

      const result = await analyzeProjects(PROJECT_FILES);

      expect('error' in result).toBe(true);
      expect(pluginCacheEntries).toEqual({});
    });

    it('retries after a failure rather than serving the cached error', async () => {
      spawnSync.mockReturnValue({ status: 1, stdout: '', stderr: 'boom' });
      await analyzeProjects(PROJECT_FILES);

      analyzerReturns(EMPTY_ANALYSIS);
      const result = await analyzeProjects(PROJECT_FILES);

      expect('error' in result).toBe(false);
      expect(spawnSync).toHaveBeenCalledTimes(2);
    });
  });
});
