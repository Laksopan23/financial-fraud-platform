using Npgsql;

namespace FinancialFraudPlatform.Audit.Ledger;

public static class AppendOnlyGuard
{
    public static async Task InstallAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION audit.reject_event_mutation() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'audit_event_mutation_forbidden'; END;
            $body$;
            DROP TRIGGER IF EXISTS deny_event_change ON audit.mt_events;
            CREATE TRIGGER deny_event_change BEFORE UPDATE OR DELETE ON audit.mt_events
                FOR EACH ROW EXECUTE FUNCTION audit.reject_event_mutation();
            DROP TRIGGER IF EXISTS deny_event_truncate ON audit.mt_events;
            CREATE TRIGGER deny_event_truncate BEFORE TRUNCATE ON audit.mt_events
                FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_event_mutation();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
