// Bills, Settings, Print preview, Bill details, Limited mode, Admin/Recovery
function BillsScreen({ onOpenDialog }) {
  const [statusFilter, setStatusFilter] = React.useState('all');
  const [hidePosted, setHidePosted] = React.useState(false);
  const [selectedSet, setSelectedSet] = React.useState(new Set());
  const [focusIdx, setFocusIdx] = React.useState(0);
  const [search, setSearch] = React.useState('');
  const [ctxMenu, setCtxMenu] = React.useState({ show: false, x: 0, y: 0, bill: null });

  // Close context menu on outside click or Escape
  React.useEffect(() => {
    const onDown = (e) => { if (!e.target.closest('[data-ctx-menu]')) setCtxMenu(m => ({...m, show: false})); };
    const onKey  = (e) => { if (e.key === 'Escape') setCtxMenu(m => ({...m, show: false})); };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('mousedown', onDown); window.removeEventListener('keydown', onKey); };
  }, []);

  const bills = SAMPLE.bills;
  const rows = bills.filter(b => {
    if (hidePosted && b.status === 'posted') return false;
    if (statusFilter !== 'all' && b.status !== statusFilter) return false;
    if (search && !b.party.toLowerCase().includes(search.toLowerCase()) && !b.no.toLowerCase().includes(search.toLowerCase())) return false;
    return true;
  });

  const counts = {
    total: 49,
    posted: 49,
    pending: 0,
    failed: 0,
  };
  const totalAmt   = 2847536;
  const postedAmt  = 2847536;
  const pendingAmt = 0;
  const failedAmt  = 0;

  return (
    <div style={{display:'flex', flexDirection:'column', height:'100%', minHeight:0, overflow:'hidden'}}>
      <div className="page-head">
        <div className="page-title">Bills &amp; Sync History</div>
        <div className="row">
          <button className="btn sm" onClick={() => onOpenDialog('billDetails')}>
            Open Details <span className="kbd kbd-inline">Enter</span>
          </button>
          <button className="btn sm">Retry Sync <span className="kbd kbd-inline">R</span></button>
          <button className="btn sm">Post to Tally <span className="kbd kbd-inline">P</span></button>
          <button className="btn sm" onClick={() => onOpenDialog('confirmRepost')}>Repost <span className="kbd kbd-inline">⇧P</span></button>
          <button className="btn sm">Edit <span className="kbd kbd-inline">E</span></button>
          <button className="btn sm">Print <span className="kbd kbd-inline">Ctrl+P</span></button>
        </div>
      </div>

      <div className="tiles">
        <div className="tile info"><div className="lbl">Total Bills</div><div className="val">{counts.total}</div><div className="sub mono">₹{fmtINR(totalAmt, 0)}</div></div>
        <div className="tile ok"><div className="lbl">Posted to Tally</div><div className="val">{counts.posted}</div><div className="sub mono">₹{fmtINR(postedAmt, 0)}</div></div>
        <div className="tile warn"><div className="lbl">Pending Sync</div><div className="val">{counts.pending}</div><div className="sub mono">₹{fmtINR(pendingAmt, 0)}</div></div>
        <div className="tile err"><div className="lbl">Failed</div><div className="val">{counts.failed}</div><div className="sub mono">₹{fmtINR(failedAmt, 0)}</div></div>
      </div>

      {/* Filter bar */}
      <div style={{display:'flex', alignItems:'center', gap:8, padding:'8px 14px', borderBottom:'1px solid var(--border)'}}>
        <span style={{fontSize:11, color:'var(--ink-muted)'}}>State</span>
        <select className="select" style={{width:110, height:26, fontSize:11.5}} value={statusFilter} onChange={e=>setStatusFilter(e.target.value)}>
          <option value="all">All</option><option value="posted">Posted</option><option value="pending">Pending</option><option value="failed">Failed</option><option value="voided">Voided</option>
        </select>
        <span style={{fontSize:11, color:'var(--ink-muted)', marginLeft:6}}>From</span>
        <div style={{display:'flex', alignItems:'center', height:26, border:'1px solid var(--border-strong)', borderRadius:3, background:'var(--bg-panel)'}}>
          <input className="input mono" style={{width:90, height:24, fontSize:11.5, border:'none', padding:'0 4px 0 8px'}} placeholder="Select a date" />
          <span style={{padding:'0 6px', color:'var(--ink-muted)', borderLeft:'1px solid var(--divider)', height:'100%', display:'inline-flex', alignItems:'center', cursor:'pointer'}}>📅</span>
        </div>
        <span style={{color:'var(--ink-muted)'}}>→</span>
        <span style={{fontSize:11, color:'var(--ink-muted)'}}>To</span>
        <div style={{display:'flex', alignItems:'center', height:26, border:'1px solid var(--border-strong)', borderRadius:3, background:'var(--bg-panel)'}}>
          <input className="input mono" style={{width:90, height:24, fontSize:11.5, border:'none', padding:'0 4px 0 8px'}} placeholder="Select a date" />
          <span style={{padding:'0 6px', color:'var(--ink-muted)', borderLeft:'1px solid var(--divider)', height:'100%', display:'inline-flex', alignItems:'center', cursor:'pointer'}}>📅</span>
        </div>
        <button className="btn sm">Apply</button>
        <button className="btn sm ghost">Clear</button>
        <label className="row" style={{fontSize:11.5, gap:4, marginLeft:6}}><input type="checkbox" checked={hidePosted} onChange={e=>setHidePosted(e.target.checked)} /> Hide posted</label>
        <label className="row" style={{fontSize:11.5, gap:4}}><input type="checkbox" /> Hide fully pushed dates</label>
        <div className="spacer" />
        <span className="hint">Showing <b className="mono" style={{color:'var(--ink)'}}>49</b> of <b className="mono" style={{color:'var(--ink)'}}>49</b> bills</span>
      </div>

      {/* Batch action bar — shown when rows selected */}
      {selectedSet.size > 0 && (
        <div style={{display:'flex', alignItems:'center', gap:8, padding:'6px 14px', background:'var(--accent-soft)', borderBottom:'1px solid var(--border)'}}>
          <span className="chip accent">{selectedSet.size} selected</span>
          <button className="btn sm" onClick={()=>setSelectedSet(new Set())}>Clear</button>
          <span className="divider-v" style={{height:18}} />
          <button className="btn sm">Retry All</button>
          <button className="btn sm" onClick={() => onOpenDialog('confirmRepost')}>Repost…</button>
          <button className="btn sm">Print</button>
        </div>
      )}

      {/* Table */}
      <div style={{flex:1, overflow:'auto', background:'var(--bg-panel)'}}>
        <table className="dt">
          <thead>
            <tr>
              <th style={{width:30}}>
                <input type="checkbox"
                  checked={rows.length > 0 && rows.every(b => selectedSet.has(b.no))}
                  onChange={e => setSelectedSet(e.target.checked ? new Set(rows.map(b=>b.no)) : new Set())}
                />
              </th>
              <th style={{width:140}}>Invoice #</th>
              <th style={{width:90}}>Status</th>
              <th>Party</th>
              <th style={{width:130}} className="num">Amount</th>
              <th style={{width:110}}>Bill Date</th>
              <th style={{width:120}}>Updated</th>
              <th>Last Error / Note</th>
            </tr>
          </thead>
          <tbody>
            {(() => {
              // Group rows by date, in original order
              const groups = [];
              const seen = new Map();
              rows.forEach(b => {
                if (!seen.has(b.date)) {
                  const g = { date: b.date, items: [] };
                  seen.set(b.date, g);
                  groups.push(g);
                } 
                seen.get(b.date).items.push(b);
              });

              const TODAY = '2026-04-25';
              const fmtGroupDate = (d) => {
                const [y,m,dd] = d.split('-').map(Number);
                const dt = new Date(y, m-1, dd);
                const wd = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'][dt.getDay()];
                const mo = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][dt.getMonth()];
                if (d === TODAY) return `Today · ${wd}, ${dd} ${mo} ${y}`;
                return `${wd}, ${dd} ${mo} ${y}`;
              };

              let runningIdx = 0;
              const out = [];
              groups.forEach(g => {
                const subTotal = g.items.reduce((a,b)=>a+b.amount, 0);
                out.push(
                  <tr key={'h-'+g.date} className="date-group-row">
                    <td colSpan={7} style={{padding:'4px 8px', fontSize:11, color:'var(--ink-soft)', fontWeight:600, background:'var(--bg-sunken)', borderBottom:'1px solid var(--border)', borderTop:'1px solid var(--border)'}}>
                      {fmtGroupDate(g.date)} <span style={{color:'var(--ink-muted)', fontWeight:400, marginLeft:6}}>({g.items.length})</span>
                    </td>
                    <td style={{padding:'4px 8px', fontSize:11.5, color:'var(--ink)', fontWeight:600, background:'var(--bg-sunken)', borderBottom:'1px solid var(--border)', borderTop:'1px solid var(--border)', textAlign:'right'}} className="num">
                      ₹{fmtINR(subTotal, 0)}
                    </td>
                  </tr>
                );
                g.items.forEach(b => {
                  const i = runningIdx++;
                  const isSel = selectedSet.has(b.no);
                  const isFocus = i === focusIdx;
                  const toggle = (e) => {
                    e.stopPropagation();
                    setSelectedSet(prev => { const n = new Set(prev); n.has(b.no) ? n.delete(b.no) : n.add(b.no); return n; });
                  };
                  const editedFlag = b.status === 'posted' && b.err === 'Edited after push';
                  out.push(
                    <tr key={b.no}
                      className={isFocus ? 'focus-row' : isSel ? 'selected' : ''}
                      onClick={() => setFocusIdx(i)}
                      onDoubleClick={() => onOpenDialog('billDetails')}
                      onContextMenu={e => { e.preventDefault(); setFocusIdx(i); setCtxMenu({ show: true, x: e.clientX, y: e.clientY, bill: b }); }}
                      style={{userSelect:'none'}}
                    >
                      <td><input type="checkbox" checked={isSel} onChange={toggle} onClick={e=>e.stopPropagation()} /></td>
                      <td className="mono">{b.no}</td>
                      <td>
                        {editedFlag && <span className="chip ok" style={{paddingRight:4}}>● Posted <span style={{color:'var(--accent)', marginLeft:2, fontStyle:'italic'}}>· edit</span></span>}
                        {b.status === 'posted' && !editedFlag && <span className="chip ok">● Posted</span>}
                        {b.status === 'pending' && <span className="chip warn">◐ Pending</span>}
                        {b.status === 'failed'  && <span className="chip err">✕ Failed</span>}
                        {b.status === 'voided'  && <span className="chip outline">— Voided</span>}
                      </td>
                      <td>{b.party}</td>
                      <td className="num">₹ {fmtINR(b.amount, 0)}</td>
                      <td className="mono muted">{b.date}</td>
                      <td className="mono muted">{b.updated}</td>
                      <td style={{color: b.status==='failed'?'var(--err)':b.status==='pending'?'var(--warn)':editedFlag?'var(--accent)':'var(--ink-muted)', fontSize:11.5}}>
                        {b.err || '—'}
                      </td>
                    </tr>
                  );
                });
              });
              return out;
            })()}
          </tbody>
        </table>
      </div>

      {/* Context menu */}
      {ctxMenu.show && (
        <div data-ctx-menu style={{
          position:'fixed', top: ctxMenu.y, left: ctxMenu.x, zIndex:200,
          background:'var(--bg-panel)', border:'1px solid var(--border-strong)',
          borderRadius:4, boxShadow:'var(--shadow-dialog)', minWidth:210, overflow:'hidden',
          fontSize:'var(--font-ui)',
        }}>
          {[
            { label: 'Open Details',        kbd: 'Enter',  action: ()=>onOpenDialog('billDetails') },
            { label: 'Edit',                kbd: 'E',      action: null },
            { sep: true },
            { label: 'Retry Sync',          kbd: 'R',      action: null, disabled: ctxMenu.bill?.status === 'posted' },
            { label: 'Post to Tally',       kbd: 'P',      action: null, disabled: ctxMenu.bill?.status === 'posted' },
            { label: 'Repost to Tally',     kbd: '⇧P',     action: null },
            { sep: true },
            { label: 'Print',               kbd: 'Ctrl+P', action: ()=>onOpenDialog('print') },
            { label: 'Revise Posted',        kbd: null,     action: null, disabled: ctxMenu.bill?.status !== 'posted' },
            { label: 'Change Bill Number',  kbd: null,     action: null },
            { sep: true },
            { label: 'Void Pending',        kbd: null,     action: null, danger: true, disabled: ctxMenu.bill?.status !== 'pending' },
            { label: 'Delete Local',        kbd: null,     action: null, danger: true },
          ].map((item, i) => item.sep
            ? <div key={i} style={{height:1, background:'var(--divider)', margin:'3px 0'}} />
            : (
              <div key={item.label}
                onClick={() => { if (!item.disabled && item.action) { item.action(); setCtxMenu(m=>({...m,show:false})); } }}
                style={{
                  display:'flex', alignItems:'center', justifyContent:'space-between',
                  padding:'5px 12px', cursor: item.disabled ? 'default' : 'pointer',
                  color: item.disabled ? 'var(--ink-disabled)' : item.danger ? 'var(--err)' : 'var(--ink)',
                  background: 'transparent',
                  gap: 24,
                }}
                onMouseEnter={e => { if (!item.disabled) e.currentTarget.style.background = 'var(--bg-hover)'; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              >
                <span>{item.label}</span>
                {item.kbd && <span className="kbd" style={{fontSize:9}}>{item.kbd}</span>}
              </div>
            )
          )}
        </div>
      )}

      {/* Footer */}
      <div style={{display:'flex', alignItems:'center', gap:8, padding:'6px 14px', borderTop:'1px solid var(--border)', background:'var(--bg-sunken)'}}>
        <span className="hint">Showing <b className="mono" style={{color:'var(--ink)'}}>49</b> of <b className="mono" style={{color:'var(--ink)'}}>49</b> bills · <span className="kbd">Right-click</span> row for actions</span>
        <div className="spacer" />
        <button className="btn ghost sm">Revise Posted</button>
        <button className="btn ghost sm">‹ Prev</button>
        <button className="btn ghost sm">Next ›</button>
        <button className="btn ghost sm">Refresh</button>
      </div>
    </div>
  );
}

// -------------------- Settings --------------------

// Shared sub-components
function SectionTitle({ children }) {
  // "Invoice · Company on Invoice" style — bold prefix, muted suffix
  const parts = String(children).split('·');
  return (
    <div style={{display:'flex', alignItems:'baseline', gap:6, marginBottom:14}}>
      <span style={{fontWeight:700, fontSize:14, color:'var(--ink)'}}>{parts[0].trim()}</span>
      {parts[1] && <><span style={{color:'var(--divider)', fontSize:14, fontWeight:300}}>·</span><span style={{fontSize:13, color:'var(--ink-muted)', fontWeight:500}}>{parts[1].trim()}</span></>}
    </div>
  );
}

function SourceBanner({ updatedAt = '2026-04-25 12:15' }) {
  return (
    <div style={{
      display:'flex', alignItems:'center', gap:8,
      padding:'5px 14px',
      background:'var(--bg-sunken)',
      borderBottom:'1px solid var(--border)',
      fontSize: 11.5,
      color:'var(--ink-muted)',
    }}>
      <span>Source:</span>
      <span className="chip accent" style={{height:16, fontSize:10, padding:'0 6px'}}>cloud</span>
      <span style={{color:'var(--divider)'}}>|</span>
      <span>Shared business settings are persisted in PostgreSQL and served to the desktop through the API.</span>
      <span style={{color:'var(--divider)'}}>|</span>
      <span>Updated <span className="mono">{updatedAt}</span></span>
      <div style={{flex:1}}/>
      <button className="btn sm">Refresh</button>
      <button className="btn sm">Edit</button>
    </div>
  );
}

function SettingsScreen() {
  const [section, setSection] = React.useState('database');
  const navItems = [
    { key:'database',    label:'Database' },
    { key:'connection',  label:'Connection' },
    { key:'invoice',     label:'Invoice' },
    { key:'printlayout', label:'PrintLayout' },
    { key:'ledgers',     label:'Ledgers' },
    { key:'masters',     label:'Masters' },
    { key:'advanced',    label:'Advanced' },
  ];

  const hasPreview = section === 'invoice' || section === 'printlayout';

  return (
    <div style={{display:'flex', flexDirection:'column', height:'100%', minHeight:0, overflow:'hidden'}}>
      <SourceBanner />
      <div style={{display:'grid', gridTemplateColumns:'160px 1fr', flex:1, minHeight:0, overflow:'hidden'}}>
        {/* Nav */}
        <div style={{background:'var(--bg-sunken)', borderRight:'1px solid var(--border)', padding:'6px 0', display:'flex', flexDirection:'column', gap:1, overflowY:'auto'}}>
          {navItems.map(n => (
            <div key={n.key}
              className={n.key === section ? 'it active' : 'it'}
              style={{
                padding:'7px 14px', cursor:'pointer', fontSize:'var(--font-ui)',
                display:'flex', alignItems:'center',
                background: n.key===section ? 'var(--accent-soft)' : 'transparent',
                color: n.key===section ? 'var(--accent)' : n.key==='advanced' ? 'var(--err)' : 'var(--ink)',
                fontWeight: n.key===section ? 600 : 400,
                boxShadow: n.key===section ? 'inset 3px 0 0 var(--accent)' : 'none',
                borderRadius: 0,
              }}
              onClick={() => setSection(n.key)}
            >
              {n.label}
              {n.key === 'advanced' && <span style={{marginLeft:4, fontSize:10}}>⚠</span>}
            </div>
          ))}
        </div>

        {/* Content — split with live preview for invoice/printlayout */}
        {hasPreview ? (
          <div style={{display:'grid', gridTemplateColumns: section==='printlayout' ? '340px 1fr' : '1fr 320px', minHeight:0, overflow:'hidden'}}>
            <div style={{overflowY:'auto', padding:'18px 20px', background:'var(--bg-panel)', borderRight: section==='printlayout' ? '1px solid var(--border)' : 'none'}}>
              {section === 'invoice'     && <SettingsInvoice />}
              {section === 'printlayout' && <SettingsPrintLayout />}
            </div>
            <div style={{borderLeft: section==='printlayout' ? 'none' : '1px solid var(--border)', background:'var(--bg-sunken)', overflowY:'auto', padding: section==='printlayout' ? '18px 20px' : 12}}>
              <LivePreviewPanel wide={section==='printlayout'} />
            </div>
          </div>
        ) : (
          <div style={{overflowY:'auto', padding:'18px 20px', background:'var(--bg-panel)'}}>
            {section === 'database'   && <SettingsDatabase />}
            {section === 'connection' && <SettingsConnection />}
            {section === 'ledgers'    && <SettingsLedgers />}
            {section === 'masters'    && <SettingsMasters />}
            {section === 'advanced'   && <SettingsAdvanced />}
          </div>
        )}
      </div>
    </div>
  );
}

// ── Database ──────────────────────────────────────────────────────────────
function SettingsDatabase() {
  return (
    <div style={{maxWidth:780}}>
      <SectionTitle>Database</SectionTitle>

      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:18}}>
        <div className="field"><label>Bootstrap API Endpoint</label><input className="input mono" defaultValue="https://api.showroom.cloud/v1" /></div>
        <div className="field"><label>Tenant Slug</label><input className="input mono" defaultValue="dda-jewels" /></div>
        <div className="field"><label>Workstation Token</label><input className="input mono" defaultValue="ws_••••••••••••a17c" /></div>
        <div className="field"><label>Last Bootstrap</label><input className="input mono readonly" readOnly defaultValue="2026-04-25 12:12 IST" /></div>
      </div>

      <div style={{display:'flex', gap:6, marginBottom:18}}>
        <button className="btn">Test Connection</button>
        <button className="btn">Re-Bootstrap</button>
        <div className="spacer" />
        <button className="btn primary">Save</button>
      </div>

      <div style={{padding:'10px 12px', background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:'var(--radius)', fontSize:11.5, color:'var(--ink-muted)', lineHeight:1.7}}>
        The cloud database is the authoritative source for shared business settings — Connection, Active Company, Numbering, Print, Ledgers, Vouchers, Items and Karat mappings. The desktop pulls a cached snapshot at start-up and refreshes on demand.
      </div>

      <div style={{marginTop:18, fontSize:10, fontWeight:700, textTransform:'uppercase', letterSpacing:'0.06em', color:'var(--ink-soft)', marginBottom:8}}>Snapshot</div>
      <table className="dt" style={{width:'100%'}}>
        <thead>
          <tr><th>Category</th><th>Rows</th><th>Fetched</th><th>Freshness</th><th></th></tr>
        </thead>
        <tbody>
          {[
            ['Companies', 3, '2026-04-25 12:12', 'fresh'],
            ['Ledgers', 397, '2026-04-25 12:12', 'aging'],
            ['Voucher Types', 33, '2026-04-25 12:12', 'aging'],
            ['Stock Items', 11, '2026-04-25 12:12', 'aging'],
          ].map(([cat, rows, fetched, fresh]) => (
            <tr key={cat}>
              <td>{cat}</td>
              <td className="mono">{rows}</td>
              <td className="mono muted">{fetched}</td>
              <td><span className={`chip ${fresh==='fresh'?'ok':'warn'}`}>{fresh}</span></td>
              <td style={{textAlign:'right'}}><button className="btn ghost sm">Refresh</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── Connection ────────────────────────────────────────────────────────────
function SettingsConnection() {
  return (
    <div style={{maxWidth:760}}>
      <SectionTitle>Connection</SectionTitle>

      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:22}}>
        <div className="field"><label>Tally Host</label><input className="input mono" defaultValue="192.168.1.13" /></div>
        <div className="field"><label>Port</label><input className="input mono" defaultValue="9000" /></div>
        <div className="field"><label>Timeout (seconds)</label><input className="input mono" defaultValue="10" /></div>
        <div className="field"><label>Active Company</label><input className="input mono readonly" readOnly defaultValue="dummy" /></div>
      </div>

      <SectionTitle>Connection · Active Company</SectionTitle>
      <div style={{display:'flex', alignItems:'center', gap:8, marginBottom:8}}>
        <select className="select" style={{flex:1}} defaultValue="dummy">
          <option>dummy</option>
          <option>SJL Main FY25-26</option>
          <option>SJL Wholesale FY25-26</option>
        </select>
        <button className="btn">Refresh from Tally</button>
        <button className="btn primary">Set Active</button>
      </div>
      <div style={{fontSize:11, color:'var(--ink-muted)', lineHeight:1.6}}>
        Cached <span className="mono" style={{color:'var(--ink)'}}>3</span> companies · Freshness <span className="chip warn" style={{height:14, fontSize:9.5, padding:'0 5px'}}>aging</span> · Fetched <span className="mono">2026-04-25 12:12</span>
      </div>
      <div style={{marginTop:8, fontSize:11, color:'var(--ink-soft)', lineHeight:1.7}}>
        The dropdown auto-populates from the cached cloud snapshot. <span style={{color:'var(--accent)', cursor:'pointer'}}>Refresh from Tally</span> re-pulls the list and updates the dropdown in one step. <span style={{color:'var(--accent)', cursor:'pointer'}}>Set Active</span> changes the active company via the dedicated settings endpoint (no Save needed).
      </div>
    </div>
  );
}

// ── Invoice ───────────────────────────────────────────────────────────────
function SettingsInvoice() {
  return (
    <div style={{maxWidth:580}}>
      <SectionTitle>Invoice · Company on Invoice</SectionTitle>
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:20}}>
        <div className="field"><label>Company Name</label><input className="input" defaultValue="DDA Jewels" /></div>
        <div className="field"><label>Address</label><input className="input" defaultValue="2/215A Swadeshi Beema Nagar, MG Road, Agra" /></div>
        <div className="field"><label>GSTIN</label><input className="input mono" defaultValue="09AARFD2610C1ZQ" /></div>
        <div className="field"><label>State</label><input className="input" defaultValue="Uttar Pradesh" /></div>
        <div className="field"><label>Phone</label><input className="input mono" defaultValue="0562-4100044" /></div>
        <div className="field"><label>Country</label><input className="input" defaultValue="India" /></div>
      </div>

      <SectionTitle>Invoice · Bank Details</SectionTitle>
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:20}}>
        <div className="field"><label>Bank Name</label><input className="input" defaultValue="ICICI Bank" /></div>
        <div className="field"><label>IFSC</label><input className="input mono" defaultValue="ICIC0006287" /></div>
        <div className="field"><label>Account</label><input className="input mono" defaultValue="628705013133" /></div>
        <div className="field"><label>UPI</label><input className="input mono" placeholder="e.g. shop@icici" /></div>
      </div>

      <SectionTitle>Invoice · Numbering</SectionTitle>
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:20}}>
        <div className="field"><label>Prefix</label><input className="input mono" defaultValue="DDAJR/26-27/" /></div>
        <div className="field"><label>Suffix</label><input className="input mono" placeholder="optional" /></div>
      </div>

      <SectionTitle>Invoice · Print Copies (defaults)</SectionTitle>
      <div style={{display:'grid', gap:6, marginBottom:20}}>
        <label style={{display:'flex',alignItems:'center',gap:8,fontSize:'var(--font-ui)'}}><input type="checkbox" defaultChecked /> Original (for customer)</label>
        <label style={{display:'flex',alignItems:'center',gap:8,fontSize:'var(--font-ui)'}}><input type="checkbox" /> Duplicate (for showroom)</label>
        <label style={{display:'flex',alignItems:'center',gap:8,fontSize:'var(--font-ui)'}}><input type="checkbox" defaultChecked /> Triplicate (for transport)</label>
      </div>

      <SectionTitle>Invoice · Layout</SectionTitle>
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'8px 16px', marginBottom:20}}>
        <div className="field"><label>Print Font Size</label><input className="input mono" defaultValue="14" /></div>
        <div className="field"><label>Terms Font Size</label><input className="input mono" defaultValue="9" /></div>
      </div>

      <SectionTitle>Invoice · Terms &amp; Conditions</SectionTitle>
      <textarea className="textarea" rows="6" defaultValue={"We hereby declare that this invoice shows the actual price of the goods described and that all particulars mentioned are true and correct.\n\nGoods once sold will be taken back only as per the return policy applicable to the product sold.\nOn return, valuation will be calculated at the rate applicable on the day of return: 22kt @ 92%, 20kt @ 84%, and 18kt @ 75.5%.\nMaking charges are charged on the prevailing 24kt gold rate.\nNo breakage guarantee of any kind shall be applicable."} />
    </div>
  );
}

// ── Print Layout ──────────────────────────────────────────────────────────
function AssetBlock({ label, filename, fields }) {
  return (
    <div style={{marginBottom:14}}>
      {/* File row */}
      <div style={{display:'flex', alignItems:'center', gap:10, padding:'8px 12px', background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:'var(--radius)', marginBottom:10}}>
        <div style={{flex:1, minWidth:0, display:'flex', flexDirection:'column'}}>
          <div style={{fontSize:9.5, textTransform:'uppercase', letterSpacing:'0.06em', color:'var(--ink-soft)', fontWeight:700, marginBottom:1}}>{label}</div>
          <div className="mono" style={{fontSize:12, color:'var(--ink)', overflow:'hidden', textOverflow:'ellipsis', whiteSpace:'nowrap'}}>{filename}</div>
        </div>
        <div style={{display:'flex', gap:6, flexShrink:0}}>
          <button className="btn sm">Browse…</button>
          <button className="btn sm">Delete</button>
        </div>
      </div>
      {/* Placement fields */}
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr 1fr 1fr', gap:'8px 16px'}}>
        {fields.map(([lbl, val]) => (
          <div className="field" key={lbl}>
            <label>{lbl} <span style={{color:'var(--ink-soft)', fontWeight:500, textTransform:'none', letterSpacing:0}}>(cm)</span></label>
            <input className="input mono" defaultValue={val} />
          </div>
        ))}
      </div>
    </div>
  );
}

function MarginDiagram({ margins }) {
  // A5-proportioned SVG showing margin shading
  const W = 80, H = 113; // A5 ratio
  const scale = 13; // cm → px (approx, just visual)
  const ml = Math.min(margins.left * scale, 20);
  const mr = Math.min(margins.right * scale, 20);
  const mt = Math.min(margins.top * scale, 20);
  const mb = Math.min(margins.bottom * scale, 20);
  return (
    <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} style={{display:'block', flexShrink:0}}>
      <rect width={W} height={H} fill="white" stroke="var(--border-strong)" strokeWidth="1"/>
      {/* margin shading */}
      <rect x={0} y={0} width={W} height={mt} fill="var(--accent-soft)" opacity="0.7"/>
      <rect x={0} y={H - mb} width={W} height={mb} fill="var(--accent-soft)" opacity="0.7"/>
      <rect x={0} y={mt} width={ml} height={H - mt - mb} fill="var(--accent-soft)" opacity="0.7"/>
      <rect x={W - mr} y={mt} width={mr} height={H - mt - mb} fill="var(--accent-soft)" opacity="0.7"/>
      {/* content area outline */}
      <rect x={ml} y={mt} width={W - ml - mr} height={H - mt - mb} fill="none" stroke="var(--accent)" strokeWidth="0.75" strokeDasharray="3 2"/>
      {/* label lines */}
      <text x={W/2} y={mt/2 + 3} textAnchor="middle" fontSize="5" fill="var(--accent)" fontFamily="JetBrains Mono, monospace">{margins.top}</text>
      <text x={W/2} y={H - mb/2 + 2} textAnchor="middle" fontSize="5" fill="var(--accent)" fontFamily="JetBrains Mono, monospace">{margins.bottom}</text>
      <text x={ml/2} y={H/2} textAnchor="middle" fontSize="5" fill="var(--accent)" fontFamily="JetBrains Mono, monospace" transform={`rotate(-90, ${ml/2}, ${H/2})`}>{margins.left}</text>
      <text x={W - mr/2} y={H/2} textAnchor="middle" fontSize="5" fill="var(--accent)" fontFamily="JetBrains Mono, monospace" transform={`rotate(90, ${W - mr/2}, ${H/2})`}>{margins.right}</text>
    </svg>
  );
}

function SettingsPrintLayout() {
  const [margins, setMargins] = React.useState({ left:'0.3', right:'0.3', top:'0.3', bottom:'0' });

  return (
    <div>
      {/* Header row */}
      <div style={{display:'flex', alignItems:'center', justifyContent:'space-between', marginBottom:4}}>
        <div style={{display:'flex', alignItems:'baseline', gap:12}}>
          <span style={{fontWeight:700, fontSize:14, color:'var(--ink)'}}>Print Layout</span>
          <span style={{fontSize:11, color:'var(--ink-muted)'}}>Updated <span className="mono">2026-04-25 12:15</span></span>
        </div>
        <div style={{display:'flex', gap:6}}>
          <button className="btn sm">Refresh</button>
          <button className="btn sm primary">Save</button>
        </div>
      </div>

      <div style={{height:14}} />

      {/* Margins */}
      <div style={{fontSize:13, fontWeight:700, color:'var(--ink)', marginBottom:8}}>Margins <span style={{fontSize:11, color:'var(--ink-muted)', fontWeight:500}}>(cm)</span></div>
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr 1fr 1fr', gap:'8px 16px', marginBottom:22}}>
        {[['Left','left'],['Right','right'],['Top','top'],['Bottom','bottom']].map(([lbl, key]) => (
          <div className="field" key={key}>
            <label>{lbl}</label>
            <input className="input mono" value={margins[key]}
              onChange={e => setMargins(m => ({...m, [key]: e.target.value}))} />
          </div>
        ))}
      </div>

      {/* Logo */}
      <div style={{fontSize:13, fontWeight:700, color:'var(--ink)', marginBottom:8}}>Logo</div>
      <AssetBlock
        label="Current File"
        filename="v1-logo.png"
        fields={[['Offset X','1.058'],['Offset Y','0'],['Width','4.286'],['Height','2.223']]}
      />

      {/* Signature */}
      <div style={{fontSize:13, fontWeight:700, color:'var(--ink)', marginTop:18, marginBottom:8}}>Signature</div>
      <AssetBlock
        label="Current File"
        filename="v1-signature.png"
        fields={[['Offset X','0.794'],['Offset Y','0'],['Width','4.233'],['Height','1.958']]}
      />

      <div style={{fontSize:11, color:'var(--ink-soft)', lineHeight:1.7, marginTop:6}}>
        Loaded · <span className="mono">2</span> asset(s)
      </div>
      <div style={{fontSize:11, color:'var(--ink-soft)', lineHeight:1.7, marginTop:8}}>
        Images are capped at 2 MB. Uploading replaces the old asset reference. Margins and placement are stored per showroom and applied by the <span style={{color:'var(--accent)', cursor:'pointer'}}>print renderer</span>.
      </div>
    </div>
  );
}

// ── Live preview pane (shared by Invoice + PrintLayout) ───────────────────
function LivePreviewPanel({ wide }) {
  const [zoom, setZoom] = React.useState(wide ? 35 : 50);

  return (
    <div style={{display:'flex', flexDirection:'column', height:'100%'}}>
      {/* Toolbar */}
      <div style={{display:'flex', alignItems:'center', justifyContent:'space-between', marginBottom:10, flexShrink:0, gap:8}}>
        <div style={{minWidth:0}}>
          <div style={{fontWeight:600, fontSize:13, color:'var(--ink)'}}>Live preview</div>
          <div style={{fontSize:11, color:'var(--ink-muted)'}}>2 pages · fit width</div>
        </div>
        <div style={{display:'flex', alignItems:'center', gap:4, flexShrink:0}}>
          <button className="btn sm" style={{minWidth:24, padding:'0 6px'}} onClick={()=>setZoom(z=>Math.max(15,z-5))}>−</button>
          <span className="mono" style={{fontSize:11, minWidth:34, textAlign:'center', color:'var(--ink-muted)'}}>{zoom}%</span>
          <button className="btn sm" style={{minWidth:24, padding:'0 6px'}} onClick={()=>setZoom(z=>Math.min(120,z+5))}>+</button>
          <button className="btn sm" onClick={()=>setZoom(35)}>Fit</button>
          <button className="btn sm" title="Refresh">R</button>
        </div>
      </div>

      {/* Paper */}
      <div style={{flex:1, overflow:'auto', background:'oklch(88% 0.004 260)', borderRadius:'var(--radius)', padding:16, display:'flex', justifyContent:'center', position:'relative'}}>
        <div style={{
          background:'white',
          width: `${zoom * 2.10}px`,   // A5 width proportional
          minHeight: `${zoom * 2.97}px`,
          boxShadow:'0 2px 12px -4px rgba(0,0,0,0.22), 0 1px 3px -1px rgba(0,0,0,0.12)',
          padding:`${zoom * 0.04}px ${zoom * 0.04}px`,
          fontFamily:'Inter, sans-serif',
          fontSize: `${zoom * 0.085}px`,
          color:'#111',
          lineHeight:1.45,
          position:'relative',
          flexShrink: 0,
        }}>

          {/* Copy label */}
          <div style={{position:'absolute', top:4, right:4, fontSize:`${zoom*0.06}px`, color:'#555', border:'0.5px solid #bbb', padding:'1px 4px', borderRadius:1, fontFamily:'JetBrains Mono,monospace'}}>
            Original for Receiver
          </div>

          {/* Logo placeholder */}
          <div style={{display:'flex', justifyContent:'space-between', alignItems:'flex-start', marginBottom:`${zoom*0.04}px`}}>
            <div style={{width:`${zoom*0.4}px`, height:`${zoom*0.22}px`, background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:1, display:'grid', placeItems:'center', flexShrink:0}}>
              <svg width="28" height="18" viewBox="0 0 28 18" fill="none"><rect width="28" height="18" rx="1" fill="#f0f0f0"/><circle cx="9" cy="7" r="3" fill="#ccc"/><path d="M2 14 8 9l5 5 4-4 7 7" stroke="#ccc" strokeWidth="1.2" fill="none"/></svg>
            </div>
            <div style={{textAlign:'right', fontSize:`${zoom*0.06}px`, color:'#555'}}>
              <div style={{fontWeight:700, fontSize:`${zoom*0.08}px`, color:'#111'}}>TAX INVOICE</div>
              <div style={{marginTop:2}}>No. <span style={{fontFamily:'JetBrains Mono,monospace'}}>DDAJR/26-27/001</span></div>
              <div>Date <span style={{fontFamily:'JetBrains Mono,monospace'}}>24/04/2026</span></div>
            </div>
          </div>

          {/* Divider */}
          <div style={{borderTop:'1px solid #ddd', marginBottom:`${zoom*0.04}px`}}/>

          {/* Company + Party */}
          <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:`${zoom*0.04}px`, marginBottom:`${zoom*0.04}px`, fontSize:`${zoom*0.065}px`}}>
            <div>
              <div style={{fontWeight:700, marginBottom:2}}>DDA Jewels</div>
              <div style={{color:'#444'}}>2/215A Swadeshi Beema Nagar</div>
              <div style={{color:'#444'}}>MG Road, Agra – 282002</div>
              <div style={{color:'#444', fontFamily:'JetBrains Mono,monospace', fontSize:`${zoom*0.055}px`}}>GSTIN 09AARFD2610C1ZQ</div>
              <div style={{color:'#444', fontFamily:'JetBrains Mono,monospace', fontSize:`${zoom*0.055}px`}}>Ph 0562-4100044</div>
            </div>
            <div>
              <div style={{fontWeight:700, marginBottom:2}}>Bill To</div>
              <div>Sample Customer</div>
              <div style={{color:'#444'}}>1/a, MG Road, Bengaluru</div>
              <div style={{color:'#444', fontFamily:'JetBrains Mono,monospace', fontSize:`${zoom*0.055}px`}}>GSTIN 29ABCDE1234F1Z5</div>
              <div style={{color:'#444', fontFamily:'JetBrains Mono,monospace', fontSize:`${zoom*0.055}px`}}>Ph +91 98765 43210</div>
            </div>
          </div>

          <div style={{borderTop:'1px solid #ddd', marginBottom:`${zoom*0.03}px`}}/>

          {/* Rate row */}
          <div style={{display:'flex', gap:`${zoom*0.06}px`, fontSize:`${zoom*0.06}px`, color:'#555', marginBottom:`${zoom*0.03}px`}}>
            <span>24kt <b style={{color:'#111', fontFamily:'JetBrains Mono,monospace'}}>₹7,780/g</b></span>
            <span>22kt <b style={{color:'#111', fontFamily:'JetBrains Mono,monospace'}}>₹7,125/g</b></span>
            <span>Payment <b style={{color:'#111'}}>Cash</b></span>
          </div>

          {/* Items table */}
          <table style={{width:'100%', borderCollapse:'collapse', fontSize:`${zoom*0.058}px`, marginBottom:`${zoom*0.04}px`}}>
            <thead>
              <tr style={{borderBottom:'1px solid #ccc', background:'#fafafa'}}>
                <th style={{textAlign:'left', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>#</th>
                <th style={{textAlign:'left', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Description</th>
                <th style={{textAlign:'right', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Gross</th>
                <th style={{textAlign:'right', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Less</th>
                <th style={{textAlign:'right', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Net</th>
                <th style={{textAlign:'center', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Purity</th>
                <th style={{textAlign:'right', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Making</th>
                <th style={{textAlign:'right', padding:`${zoom*0.02}px ${zoom*0.025}px`, fontWeight:600}}>Amount</th>
              </tr>
            </thead>
            <tbody>
              {[
                ['1','Gold Necklace (Rope)','32.800','0.950','31.850','22K','₹380','₹2,30,518'],
                ['2','Diamond Ring','5.200','1.400','3.800','18K','₹1,800','₹83,994'],
                ['3','22kt Bangle Set','28.800','0','28.800','22K','₹450','₹2,09,646'],
              ].map(([n,desc,gw,lw,nw,kt,mk,amt]) => (
                <tr key={n} style={{borderBottom:'1px dotted #e8e8e8'}}>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, fontFamily:'JetBrains Mono,monospace'}}>{n}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`}}>{desc}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'right', fontFamily:'JetBrains Mono,monospace'}}>{gw}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'right', fontFamily:'JetBrains Mono,monospace'}}>{lw}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'right', fontFamily:'JetBrains Mono,monospace'}}>{nw}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'center'}}>{kt}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'right', fontFamily:'JetBrains Mono,monospace'}}>{mk}</td>
                  <td style={{padding:`${zoom*0.02}px ${zoom*0.025}px`, textAlign:'right', fontFamily:'JetBrains Mono,monospace', fontWeight:600}}>{amt}</td>
                </tr>
              ))}
            </tbody>
          </table>

          {/* Totals */}
          <div style={{display:'grid', gridTemplateColumns:'1fr auto', gap:`${zoom*0.01}px ${zoom*0.06}px`, fontSize:`${zoom*0.065}px`, marginLeft:'auto', maxWidth:'55%'}}>
            {[
              ['Subtotal','₹5,24,158.00'],
              ['CGST @ 1.5%','₹7,862.37'],
              ['SGST @ 1.5%','₹7,862.37'],
              ['Discount','−₹3,000.00'],
              ['Round-off','−₹0.74'],
            ].map(([l,v])=>(
              <React.Fragment key={l}>
                <div style={{color:'#555'}}>{l}</div>
                <div style={{textAlign:'right', fontFamily:'JetBrains Mono,monospace'}}>{v}</div>
              </React.Fragment>
            ))}
            <div style={{gridColumn:'1/3', borderTop:'1px solid #999', marginTop:2, paddingTop:2}}/>
            <div style={{fontWeight:700}}>Total (Incl. GST)</div>
            <div style={{textAlign:'right', fontFamily:'JetBrains Mono,monospace', fontWeight:700}}>₹5,36,882</div>
          </div>

          <div style={{borderTop:'1px solid #ddd', marginTop:`${zoom*0.06}px`, paddingTop:`${zoom*0.04}px`, display:'flex', justifyContent:'space-between', alignItems:'flex-end'}}>
            {/* Bank details */}
            <div style={{fontSize:`${zoom*0.055}px`, color:'#555'}}>
              <div style={{fontWeight:600, color:'#111', marginBottom:2}}>Bank Details</div>
              <div>ICICI Bank · <span style={{fontFamily:'JetBrains Mono,monospace'}}>628705013133</span></div>
              <div>IFSC <span style={{fontFamily:'JetBrains Mono,monospace'}}>ICIC0006287</span></div>
            </div>
            {/* Signature placeholder */}
            <div style={{textAlign:'center'}}>
              <div style={{width:`${zoom*0.38}px`, height:`${zoom*0.18}px`, background:'#f5f5f5', border:'1px solid #e0e0e0', borderRadius:1, marginBottom:3, display:'grid', placeItems:'center'}}>
                <svg width="24" height="12" viewBox="0 0 24 12" fill="none"><path d="M2 8 Q6 2 10 6 Q14 10 18 4 Q20 2 22 5" stroke="#ccc" strokeWidth="1.2" fill="none"/></svg>
              </div>
              <div style={{fontSize:`${zoom*0.055}px`, color:'#555', borderTop:'1px solid #bbb', paddingTop:2}}>Authorised Signatory</div>
            </div>
          </div>

          {/* T&C */}
          <div style={{marginTop:`${zoom*0.04}px`, fontSize:`${zoom*0.048}px`, color:'#777', lineHeight:1.5}}>
            Goods once sold will be taken back only as per the return policy. Valuation calculated at rate applicable on day of return: 22kt @ 92%, 18kt @ 75.5%. E.&O.E.
          </div>
        </div>
        <div style={{position:'absolute', right:10, bottom:6, fontSize:10.5, color:'var(--ink-muted)', fontFamily:'JetBrains Mono, monospace', background:'rgba(255,255,255,0.6)', padding:'1px 5px', borderRadius:2}}>{zoom}%</div>
      </div>
    </div>
  );
}

// ── Ledgers ───────────────────────────────────────────────────────────────
function SettingsLedgers() {
  return (
    <div style={{maxWidth:780}}>
      <div style={{display:'flex', alignItems:'center', justifyContent:'space-between', marginBottom:14}}>
        <SectionTitle>Ledgers · Mappings</SectionTitle>
        <div style={{display:'flex', gap:6}}>
          <button className="btn sm">Refresh from Tally</button>
        </div>
      </div>

      {/* stat chips */}
      <div style={{display:'flex', gap:12, marginBottom:18, fontSize:11.5, color:'var(--ink-muted)'}}>
        <span>Ledgers <span className="mono" style={{color:'var(--ink)'}}>397</span></span>
        <span>Voucher types <span className="mono" style={{color:'var(--ink)'}}>33</span></span>
        <span>Freshness <span className="chip warn" style={{height:14, fontSize:9.5, padding:'0 5px'}}>aging</span></span>
        <span>Fetched <span className="mono">2026-04-25 12:12</span></span>
      </div>

      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:'10px 24px', marginBottom:10}}>
        {[
          ['Sales Ledger',           'SALES GOLD JEWELLERY'],
          ['CGST Ledger',            'CGST TAX'],
          ['Cash Ledger',            'CASH'],
          ['SGST Ledger',            'SGST TAX'],
          ['Card / UPI / Non-Cash Ledger', 'Credit and Debit Card'],
          ['Round-off Ledger',       'REBATE AND DISCOUNT'],
          ['Sales Voucher Type',     'Counter Sale'],
          ['Discount Ledger',        'Discount'],
        ].map(([label, val]) => (
          <div className="field" key={label}>
            <label>{label}</label>
            <select className="select"><option>{val}</option></select>
          </div>
        ))}
      </div>

      <div style={{fontSize:11, color:'var(--ink-soft)', lineHeight:1.7, marginTop:8}}>
        Cached ledger and voucher-type snapshots auto-populate from the cloud on load. <span style={{color:'var(--accent)', cursor:'pointer'}}>Refresh from Tally</span> re-pulls and updates the dropdowns in one step. Free-text entry still works if the snapshot is empty or stale.
      </div>
    </div>
  );
}

// ── Masters ───────────────────────────────────────────────────────────────
function MasterInlineRow({ cols, values, onChange }) {
  // cols: [{key, width, type:'text'|'select', opts:[]}]
  return (
    <tr style={{background:'var(--bg-panel)'}}>
      {cols.map((col, ci) => (
        <td key={col.key} style={{padding:0, width: col.width, borderBottom:'1px solid var(--divider)'}}>
          {col.type === 'select' ? (
            <select style={{width:'100%', height:'var(--row-h)', border:'none', background:'transparent', fontSize:'var(--font-ui)', fontFamily:'inherit', color:'var(--ink)', padding:'0 6px', cursor:'pointer'}}
              value={values[col.key] || ''} onChange={e=>onChange(col.key, e.target.value)}>
              {(col.opts||[]).map(o=><option key={o} value={o}>{o}</option>)}
            </select>
          ) : (
            <input style={{width:'100%', height:'var(--row-h)', border:'none', background:'transparent', fontSize:'var(--font-ui)', fontFamily: col.mono?'JetBrains Mono,monospace':'inherit', color:'var(--ink)', padding:'0 6px'}}
              value={values[col.key] || ''} onChange={e=>onChange(col.key, e.target.value)} />
          )}
        </td>
      ))}
    </tr>
  );
}

function SettingsMasters() {
  const [billItems, setBillItems] = React.useState([
    { name:'Stone Ring',       unit:'',   category:'gold_based', pricingMode:'wastage', wastage:12.0, labGm:0.0, stockLabel:'' },
    { name:'Plain Ring',       unit:'',   category:'gold_based', pricingMode:'wastage', wastage:10.0, labGm:0.0, stockLabel:'' },
    { name:'Machine Chain',    unit:'',   category:'gold_based', pricingMode:'wastage', wastage:8.0,  labGm:0.0, stockLabel:'' },
    { name:'Merrut Jewellery', unit:'',   category:'gold_based', pricingMode:'wastage', wastage:10.0, labGm:0.0, stockLabel:'' },
    { name:'Tops Plain',       unit:'',   category:'gold_based', pricingMode:'wastage', wastage:10.0, labGm:0.0, stockLabel:'' },
    { name:'Diamond Jewellery',unit:'gm', category:'gold_based', pricingMode:'both',    wastage:1.5,  labGm:0.0, stockLabel:'' },
    { name:'Diamond',          unit:'ct', category:'diamond',    pricingMode:'wastage', wastage:0.0,  labGm:0.0, stockLabel:'' },
  ]);
  const [karats, setKarats] = React.useState([
    { label:'18K', purity:75.5, tallyStock:'IIM Gold Jewellery 18KT' },
    { label:'20K', purity:84.0, tallyStock:'Hallmarked-Gold Jewellery 20KT' },
    { label:'22K', purity:92.0, tallyStock:'Hallmarked Gold Jewellery 22KT' },
  ]);

  const itemCols = [
    { key:'name',        width:180 },
    { key:'unit',        width:52,  type:'select', opts:['','gm','ct','pc'] },
    { key:'category',    width:110, type:'select', opts:['gold_based','diamond','silver','other'] },
    { key:'pricingMode', width:100, type:'select', opts:['wastage','labour','both','flat'] },
    { key:'wastage',     width:80,  mono:true },
    { key:'labGm',       width:70,  mono:true },
    { key:'stockLabel',  width:160 },
  ];

  const updateItem = (i, k, v) => setBillItems(ls => ls.map((r,ri) => ri===i ? {...r, [k]:v} : r));
  const updateKarat = (i, k, v) => setKarats(ls => ls.map((r,ri) => ri===i ? {...r, [k]:v} : r));

  return (
    <div style={{maxWidth:800}}>
      {/* Bill Items */}
      <div style={{display:'flex', alignItems:'baseline', justifyContent:'space-between', marginBottom:6}}>
        <SectionTitle>Masters · Bill Items</SectionTitle>
        <div style={{display:'flex', alignItems:'baseline', gap:10}}>
          <span style={{fontSize:11, color:'var(--ink-muted)'}}>Rows {billItems.length}</span>
          <button className="btn sm">↻ Tally Items</button>
        </div>
      </div>
      <div style={{border:'1px solid var(--border)', borderRadius:'var(--radius)', overflow:'hidden', marginBottom:6}}>
        <table className="dt" style={{tableLayout:'fixed', width:'100%'}}>
          <tbody>
            {billItems.map((row, i) => (
              <MasterInlineRow key={i} cols={itemCols} values={row} onChange={(k,v) => updateItem(i,k,v)} />
            ))}
          </tbody>
        </table>
      </div>
      {/* column legend */}
      <div style={{display:'grid', gridTemplateColumns:'180px 52px 110px 100px 80px 70px 1fr', fontSize:9.5, color:'var(--ink-muted)', textTransform:'uppercase', letterSpacing:'0.05em', fontWeight:600, padding:'0 0 12px', gap:0}}>
        {['Name','Unit','Category','Pricing Mode','Wastage %','Lab/Gm','Stock Mapping Label'].map(h => (
          <span key={h} style={{padding:'0 6px'}}>{h}</span>
        ))}
      </div>
      <div style={{marginBottom:20}}>
        <button className="btn sm">+ Add Item</button>
      </div>

      {/* Karats */}
      <div style={{display:'flex', alignItems:'baseline', justifyContent:'space-between', marginBottom:6}}>
        <SectionTitle>Masters · Karats</SectionTitle>
        <div style={{display:'flex', alignItems:'baseline', gap:10}}>
          <span style={{fontSize:11, color:'var(--ink-muted)'}}>Rows {karats.length}</span>
          <button className="btn sm">↻ Tally Items</button>
        </div>
      </div>
      <div style={{border:'1px solid var(--border)', borderRadius:'var(--radius)', overflow:'hidden', marginBottom:6}}>
        <table className="dt" style={{tableLayout:'fixed', width:'100%'}}>
          <tbody>
            {karats.map((row, i) => (
              <MasterInlineRow key={i}
                cols={[{key:'label',width:100},{key:'purity',width:80,mono:true},{key:'tallyStock',width:300}]}
                values={row} onChange={(k,v) => updateKarat(i,k,v)} />
            ))}
          </tbody>
        </table>
      </div>
      <div style={{display:'grid', gridTemplateColumns:'100px 80px 1fr', fontSize:9.5, color:'var(--ink-muted)', textTransform:'uppercase', letterSpacing:'0.05em', fontWeight:600, padding:'0 0 12px'}}>
        {['Label','Purity %','Tally Stock Item'].map(h => <span key={h} style={{padding:'0 6px'}}>{h}</span>)}
      </div>
      <div style={{marginBottom:20}}>
        <button className="btn sm">+ Add Karat</button>
      </div>
      <div style={{fontSize:11, color:'var(--ink-soft)', lineHeight:1.7, marginBottom:20}}>
        Bill Items describe what lines in the invoice look like (weight-based gold / diamond, pricing mode). Karats draw a label + purity to a Tally stock item for posting. Karats draw from the cached Tally Stock Items list below.
      </div>

      {/* Tally Stock Items */}
      <div style={{display:'flex', alignItems:'center', justifyContent:'space-between', marginBottom:8}}>
        <SectionTitle>Masters · Tally Stock Items</SectionTitle>
        <button className="btn sm">Refresh from Tally</button>
      </div>
      <div style={{fontSize:11.5, color:'var(--ink-muted)', marginBottom:10}}>
        Cached <span className="mono" style={{color:'var(--ink)'}}>11</span> item(s) · Freshness <span className="chip warn" style={{height:14, fontSize:9.5, padding:'0 5px'}}>aging</span> · Fetched <span className="mono">2026-04-25 12:12</span>
      </div>
      <div style={{border:'1px solid var(--border)', borderRadius:'var(--radius)', overflow:'hidden'}}>
        <table className="dt" style={{width:'100%'}}>
          <tbody>
            {[
              ['Diamond',                              '—',  'ct',  '7113'],
              ['GOLD BULLION',                          '—',  'GMS', '71081200'],
              ['GOODS SENT TO REPAIR',                  '—',  'GMS', '—'],
              ['Gold Ornaments 18kt (Semi Finish)',     '—',  'GMS', '711319'],
              ['Gold Ornaments 20kt (Semi Finish)',     '—',  'GMS', '711319'],
              ['Gold Ornaments 22kt (Semi Finish)',     '—',  'GMS', '7113'],
              ['Hallmarked Gold Jewellery 18KT',        '—',  'GMS', '711319'],
            ].map((r, i) => (
              <tr key={i}>
                <td>{r[0]}</td><td className="mono muted">{r[1]}</td><td className="mono">{r[2]}</td><td className="mono muted">{r[3]}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── Advanced ──────────────────────────────────────────────────────────────
function SettingsAdvanced() {
  const folders = [
    { label:'Log Folder',      path:'C:\\Users\\kk\\AppData\\Roaming\\ShowroomBilling\\logs' },
    { label:'App Data Folder', path:'C:\\Users\\kk\\AppData\\Roaming\\ShowroomBilling' },
    { label:'Install Folder',  path:'D:\\Projects\\Tally_Wrapper_V2\\src\\ShowroomBilling.Desktop\\bin\\Debug\\net10.0-windows\\' },
  ];
  const cloudItems = ['Connection settings','Active company','Numbering','Print settings','Ledger mappings','Voucher type settings','Item master data','Karat/stock mappings','Admin/shared operational settings'];
  const localItems = ['Bootstrap API endpoint','Protected local secrets/tokens','Logs','Workstation-local UX preferences'];
  return (
    <div style={{maxWidth:720}}>
      <SectionTitle>Advanced</SectionTitle>

      {/* Admin Mode */}
      <div style={{marginBottom:20}}>
        <div style={{fontWeight:600, fontSize:13, marginBottom:4}}>Admin Mode</div>
        <div style={{fontSize:12, color:'var(--ink-muted)', lineHeight:1.7}}>
          Press <span className="kbd">~</span> (backtick) at any time to open the Admin unlock dialog. Admin-only actions include forcing release of stale draft locks and rotating the admin passcode.
        </div>
      </div>

      {/* Logs */}
      <div style={{marginBottom:20}}>
        <div style={{fontWeight:600, fontSize:13, marginBottom:10}}>Logs &amp; Diagnostics</div>
        {folders.map(f => (
          <div key={f.label} style={{display:'flex', alignItems:'center', justifyContent:'space-between', gap:12, marginBottom:8, padding:'7px 10px', background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
            <div>
              <div style={{fontSize:10, textTransform:'uppercase', letterSpacing:'0.05em', color:'var(--ink-muted)', fontWeight:600, marginBottom:2}}>{f.label}</div>
              <div className="mono" style={{fontSize:11, color:'var(--accent)', cursor:'pointer', wordBreak:'break-all'}}>{f.path}</div>
            </div>
            <button className="btn sm" style={{flexShrink:0}}>Open</button>
          </div>
        ))}
        <div style={{fontSize:11, color:'var(--ink-soft)', lineHeight:1.7, marginTop:4}}>
          Logs are best-effort local files. If the folder is empty, the current session may not have emitted file logs yet — use the Install folder for console-redirected output.
        </div>
      </div>

      {/* Storage Scopes */}
      <div>
        <div style={{fontWeight:600, fontSize:13, marginBottom:10}}>Storage Scopes</div>
        <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:12}}>
          <div style={{padding:'10px 12px', background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
            <div style={{fontSize:10, textTransform:'uppercase', letterSpacing:'0.06em', color:'var(--accent)', fontWeight:700, marginBottom:8}}>Cloud Owned Categories</div>
            {cloudItems.map(it => (
              <div key={it} style={{display:'flex', alignItems:'center', gap:6, marginBottom:4, fontSize:12}}>
                <span style={{width:6, height:6, borderRadius:'50%', background:'var(--accent)', flexShrink:0, display:'inline-block'}}/>
                <span style={{color: it==='Ledger mappings'?'var(--accent)':'var(--ink)'}}>{it}</span>
              </div>
            ))}
          </div>
          <div style={{padding:'10px 12px', background:'var(--bg-sunken)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
            <div style={{fontSize:10, textTransform:'uppercase', letterSpacing:'0.06em', color:'var(--ink-muted)', fontWeight:700, marginBottom:8}}>Local Only Categories</div>
            {localItems.map(it => (
              <div key={it} style={{display:'flex', alignItems:'center', gap:6, marginBottom:4, fontSize:12}}>
                <span style={{width:6, height:6, borderRadius:'50%', background:'var(--border-strong)', flexShrink:0, display:'inline-block'}}/>
                <span style={{color:'var(--ink)'}}>{it}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

window.BillsScreen = BillsScreen;
window.SettingsScreen = SettingsScreen;
