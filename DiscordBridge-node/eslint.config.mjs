import js from '@eslint/js';
import tseslint from 'typescript-eslint';

// Flat config. Type-aware linting (recommendedTypeChecked) is scoped to the
// TS sources that tsconfig.json includes (src + tests); the project service
// reads that tsconfig for type information. Root build scripts (*.mjs) are not
// part of the TS project, so they only get the plain-JS recommended rules.
export default tseslint.config(
  { ignores: ['dist/', 'dist-types/', 'node_modules/'] },
  js.configs.recommended,
  {
    files: ['src/**/*.ts', 'tests/**/*.ts', 'tools/**/*.ts'],
    extends: [tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // Error discipline: only Error objects carry a stack, which file-log's
      // error(msg, err) relies on. Throwing/rejecting with non-Errors loses it.
      '@typescript-eslint/only-throw-error': 'error',
      '@typescript-eslint/prefer-promise-reject-errors': 'error',
    },
  },
  {
    // node:test's `test()` returns a promise the runner manages internally;
    // every test() call is an intentional floating promise. Disable the rule
    // for spec files rather than prefixing each call with `void`.
    files: ['tests/**/*.test.ts'],
    rules: {
      '@typescript-eslint/no-floating-promises': 'off',
    },
  },
);
