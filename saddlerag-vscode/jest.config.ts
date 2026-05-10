import type { Config } from 'jest';

const config: Config = {
    preset: 'ts-jest',
    testEnvironment: 'node',
    testMatch: ['**/__tests__/**/*.test.ts'],
    moduleNameMapper: {
        '^vscode$': '<rootDir>/src/__mocks__/vscode.ts'
    },
    collectCoverageFrom: ['src/**/*.ts', '!src/__tests__/**', '!src/__mocks__/**']
};

export default config;
