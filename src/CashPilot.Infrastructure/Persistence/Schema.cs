using Microsoft.Data.Sqlite;

namespace CashPilot.Infrastructure.Persistence;

/// <summary>Creates and upgrades the SQLite schema, versioned through PRAGMA user_version.</summary>
internal static class Schema
{
    private static readonly string[] Migrations =
    [
        // v1: transactions and learned classification rules.
        """
        CREATE TABLE transactions (
            id                 TEXT    NOT NULL PRIMARY KEY,
            dedup_key          TEXT    NOT NULL UNIQUE,
            account            TEXT    NOT NULL,
            date               TEXT    NOT NULL,
            amount_cents       INTEGER NOT NULL,
            raw_description    TEXT    NOT NULL,
            category           TEXT    NULL,
            item               TEXT    NULL,
            type               INTEGER NOT NULL,
            installment_number INTEGER NULL,
            installment_count  INTEGER NULL,
            source             TEXT    NULL,
            imported_at        TEXT    NOT NULL
        );
        CREATE INDEX ix_transactions_date ON transactions (date);
        CREATE INDEX ix_transactions_pending ON transactions (category) WHERE category IS NULL;

        CREATE TABLE classification_rules (
            id       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            kind     INTEGER NOT NULL,
            pattern  TEXT    NOT NULL,
            category TEXT    NOT NULL,
            item     TEXT    NOT NULL,
            UNIQUE (kind, pattern)
        );
        """,

        // v2: registered accounts (bank accounts, credit cards) with limits, bill days and overdraft (LIS) terms.
        """
        CREATE TABLE accounts (
            name                    TEXT    NOT NULL PRIMARY KEY,
            kind                    INTEGER NOT NULL,
            credit_limit_cents      INTEGER NULL,
            closing_day             INTEGER NULL,
            due_day                 INTEGER NULL,
            overdraft_limit_cents   INTEGER NULL,
            overdraft_free_days     INTEGER NULL,
            overdraft_monthly_rate  TEXT    NULL
        );
        """,

        // v3: manual balance anchor per account, and hand-registered payables (boletos, bills).
        """
        ALTER TABLE accounts ADD COLUMN balance_anchor_cents INTEGER NULL;
        ALTER TABLE accounts ADD COLUMN balance_anchor_date  TEXT    NULL;

        CREATE TABLE payables (
            id           TEXT    NOT NULL PRIMARY KEY,
            description  TEXT    NOT NULL,
            due_date     TEXT    NOT NULL,
            amount_cents INTEGER NOT NULL,
            paid         INTEGER NOT NULL DEFAULT 0,
            paid_date    TEXT    NULL
        );
        CREATE INDEX ix_payables_due ON payables (due_date);
        """,

        // v4: soft delete. A deleted entry keeps its dedup key, so importing the same file again does not bring it back.
        """
        ALTER TABLE transactions ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;
        """,

        // v5: late-payment terms printed on a boleto (multa and juros de mora), used by the simulator.
        """
        ALTER TABLE payables ADD COLUMN late_fee_percent TEXT NULL;
        ALTER TABLE payables ADD COLUMN late_interest_monthly_percent TEXT NULL;
        """,
    ];

    public static void Apply(SqliteConnection connection)
    {
        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[version];
                command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {version + 1};";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
