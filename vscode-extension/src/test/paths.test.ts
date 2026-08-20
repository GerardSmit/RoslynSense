import * as assert from 'assert';
import { describe, it } from 'node:test';

import { isUnder } from '../paths';

/**
 * The path containment rule, which decides whether a saved file belongs to a project.
 *
 * Plain `node --test`: nothing here touches the `vscode` API. The cases below are the two ways the
 * obvious implementation — `saved.startsWith(projectDirectory)` — gets it wrong on Windows, and
 * both are silent when they happen. Getting a false answer means on-save rediscovery never fires;
 * getting a true one means an unexpanded project is pulled through the server's load gate and a
 * cold `dotnet restore` for a file it does not contain.
 */
describe('isUnder', () => {
    /**
     * `Uri.fsPath` lower-cases the drive letter; MSBuild reports the casing the .sln was written
     * with. The two disagreeing is the normal case, not the exotic one.
     */
    it('ignores the casing of the drive letter and of the segments', () => {
        assert.strictEqual(isUnder('c:\\Sources\\App\\Tests\\UnitTest.cs', 'C:\\Sources\\App\\Tests'), true);
        assert.strictEqual(isUnder('C:\\SOURCES\\app\\tests\\UnitTest.cs', 'c:\\Sources\\App\\Tests'), true);
    });

    it('treats the separators as interchangeable', () => {
        assert.strictEqual(isUnder('C:/Sources/App/Tests/UnitTest.cs', 'C:\\Sources\\App\\Tests'), true);
    });

    /**
     * The reason the parent needs a trailing separator. Both are test projects, so both are in the
     * map this is asked about, and a prefix test hands every save in the longer one to the shorter.
     */
    it('does not count a sibling whose name merely begins with the directory name', () => {
        assert.strictEqual(
            isUnder('C:\\Sources\\Foo.Tests.Integration\\ApiTests.cs', 'C:\\Sources\\Foo.Tests'),
            false
        );
        assert.strictEqual(
            isUnder('C:\\Sources\\Foo.Tests\\ApiTests.cs', 'C:\\Sources\\Foo.Tests'),
            true
        );
    });

    /** A directory is not under itself, whether or not it was named with a trailing separator. */
    it('says no to the directory itself', () => {
        assert.strictEqual(isUnder('C:\\Sources\\Foo.Tests', 'C:\\Sources\\Foo.Tests'), false);
        assert.strictEqual(isUnder('C:\\Sources\\Foo.Tests', 'C:\\Sources\\Foo.Tests\\'), false);
    });

    it('follows the path down as far as it goes', () => {
        assert.strictEqual(
            isUnder('C:\\Sources\\Foo.Tests\\Api\\V2\\OrderTests.cs', 'C:\\Sources\\Foo.Tests'),
            true
        );
        assert.strictEqual(
            isUnder('C:\\Sources\\Bar.Tests\\OrderTests.cs', 'C:\\Sources\\Foo.Tests'),
            false
        );
    });
});
