// src/__tests__/TreeItems.test.ts
import { ServiceItem, LibraryItem, JobItem, SectionItem } from '../sidebar/TreeItems';

describe('TreeItems', () => {
    describe('ServiceItem', () => {
        it('label includes service name', () => {
            const item = new ServiceItem('MongoDB', 'running');
            expect(item.label).toContain('MongoDB');
        });

        it('shows pass icon when running', () => {
            const item = new ServiceItem('MongoDB', 'running');
            expect(JSON.stringify(item.iconPath)).toContain('pass');
        });

        it('shows error icon when not-installed', () => {
            const item = new ServiceItem('MongoDB', 'not-installed');
            expect(JSON.stringify(item.iconPath)).toContain('error');
        });
    });

    describe('LibraryItem', () => {
        it('label is library name', () => {
            const item = new LibraryItem('mongodb-driver', '3.1.2', 'Healthy');
            expect(item.label).toContain('mongodb-driver');
        });
    });

    describe('SectionItem', () => {
        it('label includes count when provided', () => {
            const item = new SectionItem('Libraries', 3);
            expect(String(item.label)).toContain('Libraries');
            expect(String(item.label)).toContain('3');
        });
    });
});
