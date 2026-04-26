// Limited mode screen + tweaks panel + root app
function LimitedModeView({ onOpenDialog, onExit }) {
  return (
    <div style={{padding:'10px 16px', overflow:'auto', height:'100%'}}>
      <div className="banner warn" style={{borderRadius:4, marginBottom:14, border:'1px solid color-mix(in oklab, var(--warn) 40%, transparent)'}}>
        <span className="chip warn" style={{height:18}}>LIMITED MODE</span>
        <span>Operating with reduced functionality — the API or Tally is unavailable. Billing actions that require persistence or posting stay blocked until connectivity is restored.</span>
      </div>

      <div className="limited-card">
        <header>
          <span className="dot err" />
          <div>
            <h2>Working without cloud &amp; Tally</h2>
            <div style={{fontSize:12, color:'var(--ink-muted)'}}>Last successful sync: 19-Apr-2026 14:02 IST · 3h 46m ago</div>
          </div>
          <div style={{marginLeft:'auto'}}>
            <button className="btn">Retry Now <span className="kbd kbd-inline">R</span></button>
          </div>
        </header>
        <div className="body">
          <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:16}}>
            <div>
              <div className="section-title" style={{color:'var(--ok)'}}>✓ Still works</div>
              <ul>
                <li><span className="dot ok" /> Reviewing cached settings and diagnostics</li>
                <li><span className="dot ok" /> Printing estimates from unsaved work</li>
                <li><span className="dot ok" /> Quick-add picker using last synced masters</li>
                <li><span className="dot ok" /> Viewing bills loaded before disconnect</li>
              </ul>
            </div>
            <div>
              <div className="section-title" style={{color:'var(--err)'}}>✕ Blocked until recovery</div>
              <ul>
                <li><span className="dot err" /> Posting to Tally</li>
                <li><span className="dot err" /> Final tax invoice numbers (cloud-allocated)</li>
                <li><span className="dot err" /> Master-data changes (ledgers, items, karat)</li>
                <li><span className="dot err" /> Voiding, reposting, deletion of older bills</li>
              </ul>
            </div>
          </div>
        </div>
        <div className="foot">
          <button className="btn" onClick={() => onOpenDialog('shortcuts')}>Open Settings → Connection</button>
          <button className="btn" onClick={onExit}>Continue in Limited Mode</button>
          <button className="btn primary">Retry Both</button>
        </div>
      </div>

      <div style={{maxWidth:680, margin:'16px auto 0', fontSize:11.5, color:'var(--ink-muted)'}}>
        <div className="section-title">Recovery checklist</div>
        <div className="panel">
          <div className="panel-body">
            <div className="timeline">
              <div className="node"><span className="bullet err" /><div className="txt"><div className="t">Cloud API unreachable</div><div className="d">DNS resolves · TCP connect fails · verify firewall / VPN</div></div><div className="time">3h 46m</div></div>
              <div className="node"><span className="bullet err" /><div className="txt"><div className="t">Tally XML endpoint unavailable</div><div className="d">Check TallyPrime and localhost XML settings</div></div><div className="time">32m</div></div>
              <div className="node"><span className="bullet" /><div className="txt"><div className="t">Pending bills require manual push</div><div className="d">Operator retries after API and Tally recover</div></div><div className="time">—</div></div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function TweaksPanel({ open, onClose, state, setState }) {
  if (!open) return null;
  const accents = [
    { id: 'indigo', val: 'oklch(45% 0.14 258)', name: 'Indigo' },
    { id: 'teal',   val: 'oklch(45% 0.12 200)', name: 'Teal' },
    { id: 'forest', val: 'oklch(42% 0.11 155)', name: 'Forest' },
    { id: 'oxblood',val: 'oklch(42% 0.14 20)',  name: 'Oxblood' },
    { id: 'ink',    val: 'oklch(28% 0.02 260)', name: 'Ink' },
  ];
  return (
    <div className="tweaks">
      <div className="tweaks-head">
        <span>Tweaks</span>
        <button className="btn ghost sm" style={{padding:'0 6px', height:20}} onClick={onClose}>✕</button>
      </div>
      <div className="tweaks-body">
        <div className="tweak-row">
          <div className="lbl">Density (Variant)</div>
          <div className="seg">
            <button className={state.density==='compact'?'on':''} onClick={()=>setState(s=>({...s, density:'compact'}))}>A · Compact</button>
            <button className={state.density==='spacious'?'on':''} onClick={()=>setState(s=>({...s, density:'spacious'}))}>B · Spacious</button>
          </div>
        </div>
        <div className="tweak-row">
          <div className="lbl">System State</div>
          <div className="seg">
            <button className={state.system==='healthy'?'on':''} onClick={()=>setState(s=>({...s, system:'healthy'}))}>Healthy</button>
            <button className={state.system==='degraded'?'on':''} onClick={()=>setState(s=>({...s, system:'degraded'}))}>Degraded</button>
            <button className={state.system==='limited'?'on':''} onClick={()=>setState(s=>({...s, system:'limited'}))}>Limited</button>
          </div>
        </div>
        <div className="tweak-row">
          <div className="lbl">Show keyboard hints</div>
          <div className="seg">
            <button className={state.hints?'on':''} onClick={()=>setState(s=>({...s, hints:true}))}>On</button>
            <button className={!state.hints?'on':''} onClick={()=>setState(s=>({...s, hints:false}))}>Off</button>
          </div>
        </div>
        <div className="tweak-row">
          <div className="lbl">Editing posted bill</div>
          <div className="seg">
            <button className={state.editingPosted?'on':''} onClick={()=>setState(s=>({...s, editingPosted:true}))}>On</button>
            <button className={!state.editingPosted?'on':''} onClick={()=>setState(s=>({...s, editingPosted:false}))}>Off</button>
          </div>
        </div>
        <div className="tweak-row">
          <div className="lbl">Accent</div>
          <div style={{display:'flex', gap:6, flexWrap:'wrap'}}>
            {accents.map(a => (
              <button key={a.id} onClick={()=>setState(s=>({...s, accent:a.val, accentId:a.id}))}
                title={a.name}
                style={{
                  width: 26, height: 26, borderRadius: 4,
                  border: state.accentId === a.id ? '2px solid var(--ink)' : '1px solid var(--border-strong)',
                  background: a.val, cursor:'pointer'
                }}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function App() {
  const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
    "density": "compact",
    "system": "degraded",
    "hints": true,
    "accent": "oklch(45% 0.14 258)",
    "accentId": "indigo",
    "editingPosted": false
  }/*EDITMODE-END*/;

  const [state, setState] = React.useState(() => {
    try { const s = localStorage.getItem('showroom-tweaks'); if (s) return { ...TWEAK_DEFAULTS, ...JSON.parse(s) }; } catch {}
    return TWEAK_DEFAULTS;
  });
  const [tab, setTab] = React.useState(() => localStorage.getItem('showroom-tab') || 'invoice');
  const [dialog, setDialog] = React.useState(null);
  const [tweaksOpen, setTweaksOpen] = React.useState(false);
  const [lineCount, setLineCount] = React.useState(2);
  const [lastSaved, setLastSaved] = React.useState('16:42:08');

  React.useEffect(() => { localStorage.setItem('showroom-tweaks', JSON.stringify(state)); }, [state]);
  React.useEffect(() => { localStorage.setItem('showroom-tab', tab); }, [tab]);

  // apply accent var
  React.useEffect(() => {
    document.documentElement.style.setProperty('--accent', state.accent);
    // derive hover/pressed/soft/ring relative
    const a = state.accent;
    document.documentElement.style.setProperty('--accent-hover', a.replace(/oklch\(\s*(\d+)%/, (m,l)=>`oklch(${Math.max(30, +l-5)}%`));
    document.documentElement.style.setProperty('--accent-pressed', a.replace(/oklch\(\s*(\d+)%/, (m,l)=>`oklch(${Math.max(25, +l-10)}%`));
  }, [state.accent]);

  // Edit-mode contract
  React.useEffect(() => {
    const onMsg = (e) => {
      const d = e.data || {};
      if (d.type === '__activate_edit_mode') setTweaksOpen(true);
      if (d.type === '__deactivate_edit_mode') setTweaksOpen(false);
    };
    window.addEventListener('message', onMsg);
    try { window.parent.postMessage({ type: '__edit_mode_available' }, '*'); } catch {}
    return () => window.removeEventListener('message', onMsg);
  }, []);

  // persist tweak changes to host for survival across reload
  React.useEffect(() => {
    try { window.parent.postMessage({ type: '__edit_mode_set_keys', edits: state }, '*'); } catch {}
  }, [state]);

  // Global shortcuts
  React.useEffect(() => {
    const onKey = (e) => {
      if (e.target && ['INPUT','TEXTAREA','SELECT'].includes(e.target.tagName)) {
        if (!(e.ctrlKey || e.metaKey) && e.key !== 'Escape' && e.key !== 'F1' && e.key !== 'F9' && e.key !== '?') return;
      }
      if ((e.ctrlKey||e.metaKey) && e.key === '1') { e.preventDefault(); setTab('invoice'); }
      if ((e.ctrlKey||e.metaKey) && e.key === '2') { e.preventDefault(); setTab('bills'); }
      if ((e.ctrlKey||e.metaKey) && e.key === '3') { e.preventDefault(); setTab('settings'); }
      if (e.key === '?') { e.preventDefault(); setDialog('shortcuts'); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const systemState = state.system;

  return (
    <div
      className={`app-frame ${state.hints ? '' : 'hidden-hints'}`}
      data-density={state.density}
      data-system={systemState}
      data-screen-label="01 App Shell"
    >
      <TitleBar company="Subramaniam Jewellers · T.Nagar" counter="COUNTER-01" operator="M. Sundaram" />
      <div>
        {systemState === 'degraded' && (
          <div className="banner warn">
            <span className="dot warn pulse" />
            <span><b>Tally unavailable</b> — posting is blocked until the operator retries after recovery.</span>
            <button className="btn sm ghost dismiss" onClick={() => setDialog('health')}>View details</button>
          </div>
        )}
        {systemState === 'limited' && (
          <div className="banner err">
            <span className="dot err" />
            <span><b>Limited mode</b> — cloud and Tally unavailable. Working in memory only.</span>
          </div>
        )}
        <NavBar tab={tab} onTab={setTab} systemState={systemState}
          onOpenShortcuts={()=>setDialog('shortcuts')}
          onOpenDialog={setDialog} />
      </div>

      <div style={{minHeight:0, overflow:'hidden'}}>
        {systemState === 'limited' ? (
          <LimitedModeView onOpenDialog={setDialog} onExit={() => setState(s=>({...s, system:'degraded'}))} />
        ) : tab === 'invoice' ? (
          <div data-screen-label="02 Invoice" style={{height:'100%'}}>
            <InvoiceScreen systemState={systemState} editingPosted={state.editingPosted} onOpenDialog={setDialog} setLineCount={setLineCount} setLastSaved={setLastSaved} />
          </div>
        ) : tab === 'bills' ? (
          <div data-screen-label="03 Bills" style={{height:'100%'}}>
            <BillsScreen onOpenDialog={setDialog} />
          </div>
        ) : (
          <div data-screen-label="04 Settings" style={{height:'100%'}}>
            <SettingsScreen />
          </div>
        )}
      </div>

      <FKeyStrip tab={systemState === 'limited' ? 'invoice' : tab} />
      <StatusBar systemState={systemState} lastSaved={lastSaved} lineCount={lineCount} />

      {dialog === 'billDetails' && <BillDetailsDialog onClose={()=>setDialog(null)} />}
      {dialog === 'print' && <PrintPreviewDialog onClose={()=>setDialog(null)} />}
      {dialog === 'postSave' && <PostSaveDialog onClose={()=>setDialog(null)} />}
      {dialog === 'admin' && <AdminUnlockDialog onClose={()=>setDialog(null)} />}
      {dialog === 'danger' && <DangerConfirmDialog onClose={()=>setDialog(null)} />}
      {dialog === 'shortcuts' && <ShortcutsDialog onClose={()=>setDialog(null)} />}
      {dialog === 'confirmRepost' && <ConfirmRepostDialog onClose={()=>setDialog(null)} />}
      {dialog === 'health' && (
        <Dialog title="System Health" onClose={()=>setDialog(null)} width={560}
          footer={<><button className="btn">Open Connection Settings</button><div className="spacer"/><button className="btn primary" onClick={()=>setDialog(null)}>Close</button></>}>
          <div style={{display:'grid', gap:10, fontSize:12.5}}>
            <div className="row"><span className={`dot ${systemState==='limited'?'err':'ok'}`} /><b>Cloud backend</b><span style={{marginLeft:'auto', color:'var(--ink-muted)'}}>{systemState==='limited'?'Offline — last 14:02':'Synced · 17:48:02'}</span></div>
            <div className="row"><span className={`dot ${systemState==='healthy'?'ok':systemState==='limited'?'err':'warn'}`} /><b>Tally XML</b><span style={{marginLeft:'auto', color:'var(--ink-muted)'}}>{systemState==='healthy'?'Reachable':systemState==='limited'?'Unavailable':'Retry needed'}</span></div>
            <div className="row"><span className="dot ok" /><b>Local workstation</b><span style={{marginLeft:'auto', color:'var(--ink-muted)'}}>Healthy · 38% CPU · 612 MB</span></div>
            <div className="row"><span className={`dot ${systemState==='healthy'?'ok':'warn'}`} /><b>Pending push</b><span style={{marginLeft:'auto', color:'var(--ink-muted)'}}>{systemState==='healthy'?'None':systemState==='limited'?'Blocked':'Manual retry required'}</span></div>
          </div>
        </Dialog>
      )}

      {/* Floating button to open tweaks (if not available via toolbar) */}
      {!tweaksOpen && (
        <button
          onClick={()=>setTweaksOpen(true)}
          className="btn sm"
          style={{position:'fixed', bottom:30, right:14, zIndex:55, boxShadow:'var(--shadow-pop)'}}
        >
          ⚙ Tweaks
        </button>
      )}
      <TweaksPanel open={tweaksOpen} onClose={()=>setTweaksOpen(false)} state={state} setState={setState} />
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
