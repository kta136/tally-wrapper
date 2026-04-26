// App Shell: titlebar, nav tabs (with icons), F-key strip, status bar
const { useState, useEffect, useRef } = React;

function TitleBar({ company, counter, operator }) {
  return (
    <div className="titlebar">
      <div className="brand">
        <span className="brand-mark">S</span>
        <span>Showroom Billing</span>
        <span style={{ color: 'var(--ink-soft)', fontWeight: 400, fontSize: 10 }}>v3.4.2</span>
      </div>
      <div className="ctx">
        <span>Company <b>{company}</b></span>
        <span style={{width:1,height:10,background:'var(--divider)',display:'inline-block',margin:'0 4px'}}></span>
        <span>Counter <b>{counter}</b></span>
        <span style={{width:1,height:10,background:'var(--divider)',display:'inline-block',margin:'0 4px'}}></span>
        <span>Operator <b>{operator}</b></span>
      </div>
      <div className="win-btns">
        <button className="win-btn" tabIndex={-1} aria-label="Minimize">
          <svg width="10" height="10" viewBox="0 0 10 10"><line x1="2" y1="5" x2="8" y2="5" stroke="currentColor" strokeWidth="1"/></svg>
        </button>
        <button className="win-btn" tabIndex={-1} aria-label="Maximize">
          <svg width="10" height="10" viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7" stroke="currentColor" strokeWidth="1" fill="none"/></svg>
        </button>
        <button className="win-btn close" tabIndex={-1} aria-label="Close">
          <svg width="10" height="10" viewBox="0 0 10 10"><line x1="1.5" y1="1.5" x2="8.5" y2="8.5" stroke="currentColor" strokeWidth="1"/><line x1="8.5" y1="1.5" x2="1.5" y2="8.5" stroke="currentColor" strokeWidth="1"/></svg>
        </button>
      </div>
    </div>
  );
}

// Tab icons
const IconInvoice = () => (
  <svg width="13" height="13" viewBox="0 0 13 13" fill="none" stroke="currentColor" strokeWidth="1.25">
    <rect x="1.5" y="1" width="10" height="11" rx="1"/>
    <line x1="3.5" y1="4" x2="9.5" y2="4"/>
    <line x1="3.5" y1="6.5" x2="9.5" y2="6.5"/>
    <line x1="3.5" y1="9" x2="7" y2="9"/>
  </svg>
);
const IconBills = () => (
  <svg width="13" height="13" viewBox="0 0 13 13" fill="none" stroke="currentColor" strokeWidth="1.25">
    <rect x="2" y="4" width="9" height="7.5" rx="1"/>
    <path d="M4 4V2.5a.5.5 0 01.5-.5h5a.5.5 0 01.5.5V4"/>
    <line x1="4.5" y1="6.5" x2="8.5" y2="6.5"/>
    <line x1="4.5" y1="8.5" x2="7" y2="8.5"/>
  </svg>
);
const IconSettings = () => (
  <svg width="13" height="13" viewBox="0 0 13 13" fill="none" stroke="currentColor" strokeWidth="1.25">
    <circle cx="6.5" cy="6.5" r="1.8"/>
    <path d="M6.5 1.5v1M6.5 10.5v1M1.5 6.5h1M10.5 6.5h1M3.1 3.1l.7.7M9.2 9.2l.7.7M9.9 3.1l-.7.7M3.8 9.2l-.7.7"/>
  </svg>
);

function NavBar({ tab, onTab, onOpenShortcuts, onOpenDialog, systemState }) {
  const tabs = [
    { key: 'invoice',  label: 'Invoice',  kbd: 'Ctrl+1', Icon: IconInvoice },
    { key: 'bills',    label: 'Bills',    kbd: 'Ctrl+2', Icon: IconBills },
    { key: 'settings', label: 'Settings', kbd: 'Ctrl+3', Icon: IconSettings },
  ];
  return (
    <div className="navbar">
      {tabs.map(t => (
        <button
          key={t.key}
          className={`nav-item ${tab === t.key ? 'active' : ''}`}
          onClick={() => onTab(t.key)}
        >
          <t.Icon />
          <span>{t.label}</span>
          <span className="kbd kbd-inline">{t.kbd}</span>
        </button>
      ))}
      <div className="nav-spacer" />
      <div className="nav-right">
        <HealthCluster systemState={systemState} onClick={onOpenDialog} />
        <div className="divider-v" style={{height:18, margin:'0 4px'}} />
        <button className="btn ghost sm" onClick={onOpenShortcuts} title="Keyboard shortcuts (?)">
          Shortcuts <span className="kbd kbd-inline">?</span>
        </button>
      </div>
    </div>
  );
}

function HealthCluster({ systemState, onClick }) {
  const s = systemState;
  const cloud  = s === 'limited'  ? { dot: 'err',  t: 'Cloud: offline' }     : { dot: 'ok',   t: 'Cloud synced' };
  const tally  = s === 'healthy'  ? { dot: 'ok',   t: 'Tally: connected' }
               : s === 'degraded' ? { dot: 'warn', t: 'Tally: reconnecting' }
                                  : { dot: 'err',  t: 'Tally: unavailable' };
  const qn     = s === 'healthy' ? 0 : s === 'degraded' ? 3 : 7;
  return (
    <>
      <button className="btn ghost sm" onClick={() => onClick('health')} title={cloud.t}>
        <span className={`dot ${cloud.dot}`}></span>
        <span className="mono" style={{fontSize:10.5}}>CLOUD</span>
      </button>
      <button className="btn ghost sm" onClick={() => onClick('health')} title={tally.t}>
        <span className={`dot ${tally.dot} ${s==='degraded'?'pulse':''}`}></span>
        <span className="mono" style={{fontSize:10.5}}>TALLY</span>
      </button>

    </>
  );
}

// F-key label strip — context-sensitive
function FKeyStrip({ tab }) {
  const maps = {
    invoice: [
      ['F2', 'Edit Rate'], ['F3', 'Item Picker'], ['F4', 'Party'],
      ['F9', 'Est. Print'], ['Ctrl+S', 'Save & Post'], ['Ctrl+N', 'New Row'],
      ['Ctrl+Del', 'Remove Row'], ['Esc', 'Cancel'], ['?', 'Shortcuts'],
    ],
    bills: [
      ['Enter', 'Details'], ['R', 'Retry Sync'], ['P', 'Post'], ['⇧P', 'Repost'],
      ['E', 'Edit'], ['Ctrl+P', 'Print'], ['/', 'Search'], ['Del', 'Void/Delete'],
      ['?', 'Shortcuts'],
    ],
    settings: [
      ['Ctrl+S', 'Save'], ['Esc', 'Cancel'], ['Tab', 'Next Field'], ['?', 'Shortcuts'],
    ],
  };
  const items = maps[tab] || maps.invoice;
  return (
    <div style={{
      height: 22,
      display: 'flex',
      alignItems: 'center',
      gap: 0,
      background: 'var(--bg-sunken)',
      borderTop: '1px solid var(--divider)',
      padding: '0 8px',
      fontFamily: "'JetBrains Mono', monospace",
      fontSize: 10.5,
      color: 'var(--ink-muted)',
      userSelect: 'none',
      overflow: 'hidden',
    }}>
      {items.map(([key, label], i) => (
        <React.Fragment key={key}>
          {i > 0 && <span style={{width:1, height:12, background:'var(--divider)', margin:'0 8px', flexShrink:0}} />}
          <span style={{display:'flex', alignItems:'center', gap:4, whiteSpace:'nowrap'}}>
            <span style={{
              background: 'var(--bg-panel)',
              border: '1px solid var(--border-strong)',
              borderBottom: '2px solid var(--border-strong)',
              borderRadius: 2,
              padding: '0 5px',
              fontSize: 9.5,
              fontWeight: 700,
              color: 'var(--ink)',
              minWidth: 20,
              textAlign: 'center',
              lineHeight: '15px',
              display: 'inline-block',
            }}>{key}</span>
            <span>{label}</span>
          </span>
        </React.Fragment>
      ))}
    </div>
  );
}

function StatusBar({ systemState, lastSaved, lineCount }) {
  const [time, setTime] = React.useState(() => new Date().toLocaleTimeString('en-IN', {hour12:false}));
  React.useEffect(() => {
    const t = setInterval(() => setTime(new Date().toLocaleTimeString('en-IN', {hour12:false})), 1000);
    return () => clearInterval(t);
  }, []);
  return (
    <div className="statusbar">
      <span className="chip-text">READY</span>
      <span className="sep" />
      <span>24kt ₹{fmtINR(7780)}/g</span>
      <span className="sep" />
      <span>22kt ₹{fmtINR(7125)}/g</span>
      <span className="sep" />
      <span>18kt ₹{fmtINR(5830)}/g</span>
      <span className="sep" />
      <span>LINES <span className="mono">{String(lineCount || 0).padStart(2,'0')}</span></span>
      <span className="sep" />
      <span>LAST SAVE <span className="mono">{lastSaved}</span></span>
      <span className="grow" />
      <span>FY 2025-26</span>
      <span className="sep" />
      <span>WS: COUNTER-01</span>
      <span className="sep" />
      <span className="mono">{time} IST</span>
    </div>
  );
}

Object.assign(window, { TitleBar, NavBar, StatusBar, HealthCluster, FKeyStrip });
