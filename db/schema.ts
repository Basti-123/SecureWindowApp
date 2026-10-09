import { integer, sqliteTable, text } from 'drizzle-orm/sqlite-core';

export const downloadEvents = sqliteTable('download_events', {
  id: integer('id').primaryKey({ autoIncrement: true }),
  appVersion: text('app_version').notNull(),
  platform: text('platform').notNull(),
  downloadedAt: text('downloaded_at').notNull().default('CURRENT_TIMESTAMP'),
});
