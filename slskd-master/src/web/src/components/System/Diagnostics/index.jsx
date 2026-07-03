import { getTransferDiagnostics } from '../../../lib/diagnostics';
import { LoaderSegment } from '../../Shared';
import React, { useEffect, useState } from 'react';
import { Icon, Table, Message, Header, Divider } from 'semantic-ui-react';

const formatTimeSpan = (ts) => {
  if (!ts) return '-';
  if (typeof ts === 'string') {
    const parts = ts.split(':');
    if (parts.length === 3) {
      const [h = 0, m = 0, s = 0] = parts.map(Number);
      const totalMs = ((h * 3600) + (m * 60) + s) * 1000;
      if (totalMs >= 60000) return `${(totalMs / 60000).toFixed(1)}m`;
      if (totalMs >= 1000) return `${(totalMs / 1000).toFixed(1)}s`;
      return `${totalMs}ms`;
    }
    return ts;
  }
  const ms = Number(ts);
  if (Number.isNaN(ms)) return String(ts);
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  return `${(ms / 60000).toFixed(1)}m`;
};

const TableSection = ({ title, icon, color, headers, rows, empty }) => (
  <>
    <Header as="h4">
      <Icon name={icon} color={color} />
      <Header.Content>
        {title}
        <Header.Subheader>{rows?.length ?? 0} entries</Header.Subheader>
      </Header.Content>
    </Header>
    <Table compact="very" unstackable>
      <Table.Header>
        <Table.Row>
          {headers.map((h, i) => (
            <Table.HeaderCell key={i}>{h}</Table.HeaderCell>
          ))}
        </Table.Row>
      </Table.Header>
      <Table.Body>
        {(!rows || rows.length === 0) ? (
          <Table.Row>
            <Table.Cell colSpan={99} style={{ opacity: 0.5, textAlign: 'center' }}>
              {empty || 'None'}
            </Table.Cell>
          </Table.Row>
        ) : (
          rows.map((row, i) => (
            <Table.Row key={row.Id || i}>
              {row.cells.map((cell, j) => (
                <Table.Cell key={j} style={cell.style}>{cell.content}</Table.Cell>
              ))}
            </Table.Row>
          ))
        )}
      </Table.Body>
    </Table>
  </>
);

const ConfigRow = ({ label, value }) => (
  <Table.Row>
    <Table.Cell style={{ fontWeight: 'bold', whiteSpace: 'nowrap' }}>{label}</Table.Cell>
    <Table.Cell>{value}</Table.Cell>
  </Table.Row>
);

const Diagnostics = () => {
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const fetch = async () => {
    setLoading(true);
    setError(null);
    try {
      const result = await getTransferDiagnostics();
      setData(result);
    } catch (e) {
      setError(e.message || 'Failed to fetch diagnostics');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetch();
  }, []);

  if (loading) return <LoaderSegment />;

  if (error) {
    return (
      <Message negative>
        <Message.Header>Failed to load diagnostics</Message.Header>
        <p>{error}</p>
      </Message>
    );
  }

  if (!data) return null;

  const retry = data.retryConfiguration ?? data.RetryConfiguration ?? {};
  const ar = data.autoReplaceConfiguration ?? data.AutoReplaceConfiguration ?? {};
  const arMatch = ar.match ?? {};
  const arSearch = ar.search ?? {};
  const retrying = data.currentlyRetrying ?? data.CurrentlyRetrying ?? [];
  const exhausted = data.retriesExhausted ?? data.RetriesExhausted ?? [];
  const replaced = data.autoReplaced ?? data.AutoReplaced ?? [];
  const stall = data.stallDetectorState ?? data.StallDetectorState ?? {};

  const configTable = (items) => (
    <Table compact="very" unstackable>
      <Table.Body>
        {items.map((item, i) => (
          <ConfigRow key={i} label={item.label} value={item.value} />
        ))}
      </Table.Body>
    </Table>
  );

  const toCells = (items, fields) =>
    items.map((item) => ({
      cells: fields.map((f) => {
        let val = f.accessor(item);
        if (f.format) val = f.format(val);
        return { content: val, style: f.style };
      }),
    }));

  const stateColor = (state) => {
    if (!state) return 'grey';
    if (state.includes('Succeeded')) return 'green';
    if (state.includes('Errored') || state.includes('TimedOut') || state.includes('Rejected') || state.includes('Cancelled')) return 'red';
    if (state.includes('InProgress')) return 'blue';
    if (state.includes('Queued')) return 'yellow';
    return 'grey';
  };

  const stateLabel = (state) => (
    state ? <span style={{ color: stateColor(state) }}>{state}</span> : '-'
  );

  return (
    <>
      <div className="header-buttons">
        <span style={{ float: 'left', opacity: 0.6, fontSize: '0.9em' }}>
          <Icon name="refresh" /> Data refreshes on page load
        </span>
      </div>

      <Header as="h3">
        <Icon name="settings" />
        <Header.Content>Configuration</Header.Content>
      </Header>

      <Header as="h4">
        <Icon name="sync alternate" color="grey" />
        <Header.Content>Retry Configuration</Header.Content>
      </Header>
      {configTable([
        { label: 'Attempts', value: retry.attempts },
        { label: 'Base Delay', value: formatTimeSpan(retry.delay) },
        { label: 'Max Delay', value: formatTimeSpan(retry.maxDelay) },
        { label: 'Partial Strategy', value: retry.partial },
      ])}

      <Header as="h4">
        <Icon name="exchange" color="grey" />
        <Header.Content>Auto-Replace Configuration</Header.Content>
      </Header>
      {configTable([
        { label: 'Enabled', value: ar.enabled ? 'Yes' : 'No' },
        { label: 'On Failure', value: ar.onFailure ? 'Yes' : 'No' },
        { label: 'On Stall', value: ar.onStall ? 'Yes' : 'No' },
        { label: 'Max Candidates', value: ar.maxCandidates },
        { label: 'Stall Timeout', value: formatTimeSpan(ar.stallTimeout) },
        { label: 'Queue Stall Timeout', value: formatTimeSpan(ar.queueStallTimeout) },
        { label: 'Max Age', value: formatTimeSpan(ar.maxAge) },
        { label: 'Search Cooldown', value: formatTimeSpan(ar.searchCooldown) },
        { label: 'Require Exact Size', value: arMatch.requireExactSize ? 'Yes' : 'No' },
        { label: 'Size Tolerance', value: `${arMatch.sizeToleranceBytes} bytes` },
        { label: 'Require Same Extension', value: arMatch.requireSameExtension ? 'Yes' : 'No' },
        { label: 'Require Free Slot', value: arMatch.requireFreeUploadSlot ? 'Yes' : 'No' },
        { label: 'Min Upload Speed', value: `${arMatch.minimumUploadSpeed} B/s` },
        { label: 'Search Timeout', value: formatTimeSpan(arSearch.timeout) },
        { label: 'Search Response Limit', value: arSearch.responseLimit },
      ])}

      <Divider />

      <Header as="h3">
        <Icon name="bug" />
        <Header.Content>Runtime State</Header.Content>
      </Header>

      <Message info>
        <Message.Content>
          These tables show downloads currently being retried, those that exhausted retries,
          transfers created by auto-replace, and transfers being monitored by the stall detector.
        </Message.Content>
      </Message>

      <TableSection
        title="Currently Retrying"
        icon="sync"
        color="blue"
        headers={['Filename', 'Username', 'Attempt', 'State']}
        rows={toCells(retrying, [
          { accessor: (r) => r.filename },
          { accessor: (r) => r.username },
          { accessor: (r) => `${r.attempts} / ${r.maxAttempts}` },
          { accessor: (r) => stateLabel(r.state) },
        ])}
        empty="No transfers are currently being retried"
      />

      <TableSection
        title="Retries Exhausted (Eligible for Auto-Replace)"
        icon="times circle"
        color="red"
        headers={['Filename', 'Username', 'Attempts', 'State', 'Ended']}
        rows={toCells(exhausted, [
          { accessor: (r) => r.filename },
          { accessor: (r) => r.username },
          { accessor: (r) => `${r.attempts} / ${r.maxAttempts}` },
          { accessor: (r) => stateLabel(r.state) },
          { accessor: (r) => r.ended ? new Date(r.ended).toLocaleString() : '-' },
        ])}
        empty="No transfers with exhausted retries"
      />

      <TableSection
        title="Auto-Replaced Transfers"
        icon="exchange"
        color="teal"
        headers={['Filename', 'Source', 'Replaces', 'Attempts', 'State']}
        rows={toCells(replaced, [
          { accessor: (r) => r.filename },
          { accessor: (r) => r.username },
          { accessor: (r) => r.replacesId ? r.replacesId.substring(0, 8) + '...' : '-' },
          { accessor: (r) => r.replacementAttempts },
          { accessor: (r) => stateLabel(r.state) },
        ])}
        empty="No auto-replaced transfers"
      />

      <TableSection
        title="Stall Detector - Tracked Transfers"
        icon="hourglass half"
        color="orange"
        headers={['Transfer ID', 'Bytes', 'Place in Queue', 'Last Changed', 'Stalled For']}
        rows={toCells(stall.tracked ?? [], [
          { accessor: (r) => r.transferId ? r.transferId.substring(0, 8) + '...' : '-', style: { fontFamily: 'monospace' } },
          { accessor: (r) => r.bytes != null ? Number(r.bytes).toLocaleString() : '-' },
          { accessor: (r) => r.placeInQueue != null ? r.placeInQueue : '-' },
          { accessor: (r) => r.lastChangedUtc ? new Date(r.lastChangedUtc).toLocaleString() : '-' },
          { accessor: (r) => r.stalledFor ? formatTimeSpan(r.stalledFor) : '-' },
        ])}
        empty="Stall detector is not currently tracking any transfers"
      />
    </>
  );
};

export default Diagnostics;
