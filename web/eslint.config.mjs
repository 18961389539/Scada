import tseslint from 'typescript-eslint'
import vueParser from 'vue-eslint-parser'
import vue from 'eslint-plugin-vue'

// 只开三条与回归强相关的规则；语法/风格交给 vue-tsc、Prettier 与 hygiene-check。
// 存量告警允许通过（warn），新引入的 error 会阻断。
export default [
  {
    ignores: ['dist/**', 'test-results/**', 'node_modules/**', 'src/api/contract.ts']
  },
  {
    files: ['src/**/*.{ts,vue}', 'tests/**/*.ts'],
    languageOptions: {
      parser: vueParser,
      parserOptions: {
        parser: tseslint.parser,
        ecmaVersion: 'latest',
        sourceType: 'module',
        extraFileExtensions: ['.vue']
      }
    },
    plugins: { '@typescript-eslint': tseslint.plugin, vue },
    rules: {
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'none' }],
      'vue/no-mutating-props': 'error'
    }
  }
]
