// StatusPoller.test.ts
import { StatusPoller } from '../services/StatusPoller';

jest.useFakeTimers();

describe('StatusPoller', () => {
    let fetchMock: jest.Mock;

    beforeEach(() => {
        fetchMock = jest.fn();
        global.fetch = fetchMock;
    });

    afterEach(() => {
        jest.clearAllTimers();
    });

    it('emits saddlerag=running when /health returns 200', async () => {
        fetchMock.mockImplementation((url: string) => {
            if (url.includes('/health')) {
                return Promise.resolve({ ok: true, json: async () => ({ status: 'Healthy' }) });
            }
            return Promise.resolve({ ok: true, json: async () => ({ libraries: [], activeJobs: [] }) });
        });

        const poller = new StatusPoller('http://localhost:6100');
        const stateReceived: string[] = [];
        poller.onStateChange((s) => stateReceived.push(s.saddlerag));

        await poller.pollNow();

        expect(stateReceived[stateReceived.length - 1]).toBe('running');
    });

    it('emits saddlerag=stopped when /health fetch fails', async () => {
        fetchMock.mockImplementation((url: string) => {
            if (url.includes('/health')) {
                return Promise.reject(new Error('ECONNREFUSED'));
            }
            return Promise.resolve({ ok: true, json: async () => ({ libraries: [], activeJobs: [] }) });
        });

        const poller = new StatusPoller('http://localhost:6100');
        const stateReceived: string[] = [];
        poller.onStateChange((s) => stateReceived.push(s.saddlerag));

        await poller.pollNow();

        expect(stateReceived).toContain('stopped');
    });
});
