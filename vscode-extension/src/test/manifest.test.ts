import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { describe, it } from 'node:test';

describe('extension identity', () => {
    it('publishes under the established GerardSmit publisher', () => {
        const manifestPath = path.resolve(__dirname, '..', '..', 'package.json');
        const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as {
            name?: string;
            publisher?: string;
        };

        assert.strictEqual(`${manifest.publisher}.${manifest.name}`, 'GerardSmit.roslyn-sense');
    });
});
