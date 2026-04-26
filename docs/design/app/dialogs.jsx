// Dialogs: bill details, print preview, limited mode, admin unlock, shortcuts, dangerous confirm
function Dialog({ title, width, onClose, children, footer, meta }) {
  React.useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="scrim" onClick={(e)=>{ if(e.target.classList.contains('scrim')) onClose(); }}>
      <div className="dialog" style={{ width: width || 560 }} role="dialog">
        <div className="dialog-head">
          <div className="dialog-title">{title}</div>
          <div className="row" style={{gap:8, color:'var(--ink-muted)', fontSize:11}}>
            {meta}
            <button className="btn ghost sm" onClick={onClose} title="Close (Esc)">✕</button>
          </div>
        </div>
        <div className="dialog-body">{children}</div>
        {footer && <div className="dialog-foot">{footer}</div>}
      </div>
    </div>
  );
}

function BillDetailsDialog({ onClose }) {
  // Timeline events matching the screenshot reference
  const events = [
    { kind:'saved',    title:'Bill saved',       sub:'Invoice DDAJR/26-27/50',                                              date:'25 Apr', time:'17:44' },
    { kind:'pushReq',  title:'Push requested',   sub:'Context menu push',                                                   date:'25 Apr', time:'17:44' },
    { kind:'tallyErr', title:'Tally Failed',     sub:'TALLY_LINEERROR: Stock Item "HM Gold Jewellery 18KT" does not exist!',date:'25 Apr', time:'17:44' },
    { kind:'pushReq',  title:'Push requested',   sub:'Context menu retry',                                                  date:'25 Apr', time:'17:45' },
    { kind:'tallyErr', title:'Tally Failed',     sub:'TALLY_LINEERROR: Stock Item "HM Gold Jewellery 18KT" does not exist!',date:'25 Apr', time:'17:45' },
    { kind:'pushReq',  title:'Push requested',   sub:'retry',                                                               date:'25 Apr', time:'17:45' },
    { kind:'tallyErr', title:'Tally Failed',     sub:'TALLY_LINEERROR: Stock Item "HM Gold Jewellery 18KT" does not exist!',date:'25 Apr', time:'17:45' },
    { kind:'reopen',   title:'Bill Edit Reopened',sub:'Invoice DDAJR/26-27/50',                                              date:'25 Apr', time:'17:45' },
    { kind:'pushReq',  title:'Push requested',   sub:'Context menu push',                                                   date:'25 Apr', time:'17:45' },
    { kind:'posted',   title:'Tally Posted',     sub:'Invoice DDAJR/26-27/50',                                              date:'25 Apr', time:'17:45' },
  ];
  const dotFor = k => ({
    saved:    { bg:'var(--accent)',  ring:'var(--accent-soft)' },
    pushReq:  { bg:'#e8a13a',        ring:'rgba(232,161,58,0.18)' },
    tallyErr: { bg:'var(--err)',     ring:'rgba(220,38,38,0.16)' },
    reopen:   { bg:'var(--accent)',  ring:'var(--accent-soft)' },
    posted:   { bg:'var(--ok)',      ring:'rgba(22,163,74,0.18)' },
  }[k]);

  return (
    <Dialog
      title={<><span>Bill Details</span> <span className="chip ok" style={{marginLeft:6}}>posted</span></>}
      width={880}
      onClose={onClose}
      meta={<span className="mono">DDAJR/26-27/50</span>}
      footer={<>
        <div className="spacer" />
        <button className="btn">Retry</button>
        <button className="btn">Print…</button>
        <button className="btn" onClick={onClose}>Close</button>
        <button className="btn primary">Repost to Tally</button>
      </>}
    >
      <div style={{display:'grid', gridTemplateColumns:'1fr 1fr', gap:18, minHeight:480}}>
        {/* LEFT: Summary / Line items / Notes + Totals */}
        <div style={{display:'flex', flexDirection:'column', gap:14}}>
          {/* Summary */}
          <div>
            <div style={{fontSize:10, fontWeight:700, letterSpacing:'0.08em', textTransform:'uppercase', color:'var(--ink-soft)', marginBottom:8}}>Summary</div>
            <div style={{display:'grid', gridTemplateColumns:'80px 1fr', rowGap:8, columnGap:14, fontSize:12}}>
              <div style={{color:'var(--ink-muted)'}}>Party</div>
              <div style={{fontWeight:600}}>Walk-in Customer</div>

              <div style={{color:'var(--ink-muted)'}}>Bill Date</div>
              <div className="mono">2026-04-25</div>

              <div style={{color:'var(--ink-muted)'}}>Created</div>
              <div className="mono">2026-04-25 12:14</div>

              <div style={{color:'var(--ink-muted)'}}>Fiscal Year</div>
              <div className="mono">2026-27</div>
            </div>
          </div>

          {/* Line items */}
          <div>
            <div style={{fontSize:10, fontWeight:700, letterSpacing:'0.08em', textTransform:'uppercase', color:'var(--ink-soft)', marginBottom:8}}>Line Items</div>
            <div style={{border:'1px solid var(--border)', borderRadius:'var(--radius)', overflow:'hidden'}}>
              <table className="dt" style={{margin:0}}>
                <thead>
                  <tr style={{background:'var(--bg-sunken)'}}>
                    <th style={{textAlign:'left'}}>Item</th>
                    <th className="num" style={{width:60}}>Qty</th>
                    <th style={{width:50}}>Karat</th>
                    <th className="num" style={{width:90}}>Rate</th>
                  </tr>
                </thead>
                <tbody>
                  <tr><td>Diamond Jewellery</td><td className="num mono">2.500</td><td>18K</td><td className="num mono">12,012</td></tr>
                  <tr><td>Diamond</td><td className="num mono">2.500</td><td></td><td className="num mono">35,000</td></tr>
                </tbody>
              </table>
            </div>
          </div>

          {/* Notes + Totals (2 columns inside) */}
          <div style={{display:'grid', gridTemplateColumns:'1fr 1.1fr', gap:18}}>
            <div>
              <div style={{fontSize:10, fontWeight:700, letterSpacing:'0.08em', textTransform:'uppercase', color:'var(--ink-soft)', marginBottom:8}}>Notes</div>
              <div style={{fontSize:12, color:'var(--ink-muted)', fontStyle:'italic'}}>No operator notes.</div>
            </div>
            <div style={{display:'grid', gridTemplateColumns:'1fr auto', rowGap:5, columnGap:14, fontSize:12, alignSelf:'start'}}>
              <div style={{color:'var(--ink-muted)'}}>Subtotal</div>
              <div className="mono" style={{textAlign:'right'}}>₹ 1,14,592.23</div>

              <div style={{color:'var(--ink-muted)'}}>Tax</div>
              <div className="mono" style={{textAlign:'right'}}>₹ 3,437.76</div>

              <div style={{color:'var(--ink-muted)'}}>Discount</div>
              <div className="mono" style={{textAlign:'right', color:'var(--err)'}}>− ₹ 0.00</div>

              <div style={{color:'var(--ink-muted)'}}>Round Off</div>
              <div className="mono" style={{textAlign:'right'}}>0.01</div>

              <div style={{borderTop:'1px solid var(--border)', paddingTop:6, marginTop:2, fontWeight:700}}>Grand Total</div>
              <div className="mono" style={{textAlign:'right', borderTop:'1px solid var(--border)', paddingTop:6, marginTop:2, fontWeight:700, fontSize:14}}>₹ 1,18,030</div>
            </div>
          </div>
        </div>

        {/* RIGHT: Timeline */}
        <div style={{borderLeft:'1px solid var(--border)', paddingLeft:18, display:'flex', flexDirection:'column', minHeight:0}}>
          <div style={{fontSize:10, fontWeight:700, letterSpacing:'0.08em', textTransform:'uppercase', color:'var(--ink-soft)', marginBottom:10}}>Timeline</div>
          <div style={{flex:1, overflowY:'auto', position:'relative', paddingRight:6}}>
            {/* vertical rail */}
            <div style={{position:'absolute', left:5, top:6, bottom:6, width:1, background:'var(--divider)'}} />
            {events.map((e, i) => {
              const c = dotFor(e.kind);
              const isErr = e.kind === 'tallyErr';
              return (
                <div key={i} style={{display:'grid', gridTemplateColumns:'18px 1fr auto', gap:8, padding:'6px 0', position:'relative', alignItems:'flex-start'}}>
                  {/* dot */}
                  <div style={{position:'relative', height:18, display:'flex', alignItems:'center', justifyContent:'flex-start'}}>
                    <span style={{
                      width:11, height:11, borderRadius:'50%', background:c.bg,
                      boxShadow:`0 0 0 3px ${c.ring}, 0 0 0 1px var(--bg-panel)`,
                      display:'inline-block', position:'relative', zIndex:1,
                    }} />
                  </div>
                  {/* text */}
                  <div style={{minWidth:0}}>
                    <div style={{fontSize:12, fontWeight:600, color: isErr ? 'var(--err)' : 'var(--ink)', lineHeight:1.3}}>{e.title}</div>
                    <div style={{
                      fontSize:11, color: isErr ? '#9b2424' : 'var(--ink-muted)',
                      lineHeight:1.4, marginTop:2,
                      fontFamily: isErr ? 'JetBrains Mono, monospace' : 'inherit',
                      wordBreak: isErr ? 'break-word' : 'normal',
                    }}>{e.sub}</div>
                  </div>
                  {/* time */}
                  <div style={{fontSize:10.5, color:'var(--ink-muted)', fontFamily:'JetBrains Mono, monospace', whiteSpace:'nowrap', paddingTop:2}}>
                    {e.date} <span style={{color:'var(--ink-soft)'}}>{e.time}</span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </Dialog>
  );
}

function PrintPreviewDialog({ onClose, isEstimate = true }) {
  const [copies, setCopies] = React.useState({ original: true, duplicate: true, triplicate: false });
  const [printer, setPrinter] = React.useState('Epson TM-U220 (Counter-01)');
  const [estimate, setEstimate] = React.useState(isEstimate);
  return (
    <Dialog
      title="Print Preview"
      width={820}
      onClose={onClose}
      meta={<span className="mono">SR/25-26/0143</span>}
      footer={<>
        <button className="btn">Save as PDF <span className="kbd kbd-inline">Ctrl+Shift+S</span></button>
        <div className="spacer" />
        <button className="btn" onClick={onClose}>Close <span className="kbd kbd-inline">Esc</span></button>
        <button className="btn primary">Print <span className="kbd kbd-inline">Enter</span></button>
      </>}
    >
      <div style={{display:'grid', gridTemplateColumns:'1fr 260px', gap:16}}>
        <div style={{background:'var(--bg-sunken)', padding:16, borderRadius:4, display:'grid', placeItems:'center', border:'1px solid var(--border)', minHeight:540, position:'relative'}}>
          <div className="preview-paper" style={{position:'relative'}}>
            {estimate && <div className="preview-stamp">ESTIMATE</div>}
            <div style={{display:'flex', justifyContent:'space-between', alignItems:'flex-start'}}>
              <div>
                <h3 style={{fontSize:14, letterSpacing:0.02}}>SUBRAMANIAM JEWELLERS</h3>
                <div style={{fontSize:9, color:'#444'}}>114, Ranganathan St · T.Nagar, Chennai 600017</div>
                <div style={{fontSize:9, color:'#444'}}>GSTIN 33AABCS4567G1ZQ · HUID 6311</div>
              </div>
              <div style={{textAlign:'right', fontSize:9}}>
                <div><b>{estimate ? 'ESTIMATE' : 'TAX INVOICE'}</b></div>
                <div>No. SR/25-26/0143</div>
                <div>Dt. 19-Apr-2026</div>
              </div>
            </div>
            <hr />
            <div style={{fontSize:9}}>
              <b>To:</b> Meera Subramaniam &nbsp; · &nbsp; Payment: UPI &nbsp; · &nbsp; 24kt: ₹7,780/g
            </div>
            <hr />
            <table>
              <thead><tr><th>#</th><th>Item</th><th className="num">Wt</th><th className="num">Rate</th><th className="num">Amt</th></tr></thead>
              <tbody>
                <tr><td>1</td><td>22kt Gold Chain Rope 18"</td><td className="num">12.450</td><td className="num">7,695</td><td className="num">95,802</td></tr>
                <tr><td>2</td><td>22kt Lakshmi Pendant</td><td className="num">4.120</td><td className="num">7,766</td><td className="num">32,057</td></tr>
              </tbody>
            </table>
            <hr />
            <div style={{display:'grid', gridTemplateColumns:'1fr auto', rowGap:1, columnGap:10, fontSize:9}}>
              <div>Subtotal</div><div className="num">1,27,859.00</div>
              <div>CGST 1.5%</div><div className="num">1,917.89</div>
              <div>SGST 1.5%</div><div className="num">1,917.89</div>
              <div>Discount</div><div className="num">− 500.00</div>
              <div>Round Off</div><div className="num">+ 0.22</div>
              <div style={{fontWeight:700, borderTop:'1px solid #000', paddingTop:2}}>GRAND TOTAL</div>
              <div className="num" style={{fontWeight:700, borderTop:'1px solid #000', paddingTop:2}}>₹ 1,31,195</div>
            </div>
            <hr />
            <div style={{fontSize:8, color:'#555'}}>E. &amp; O.E. · Goods once sold cannot be returned · Hallmark BIS 916 unless specified</div>
            <div style={{marginTop:30, display:'flex', justifyContent:'space-between', fontSize:8}}>
              <div>Customer Signature</div><div>For Subramaniam Jewellers</div>
            </div>
          </div>
        </div>

        <div style={{display:'flex', flexDirection:'column', gap:12}}>
          <div className="field">
            <label>Document Type</label>
            <div className="seg">
              <button className={estimate?'on':''} onClick={()=>setEstimate(true)}>Estimate</button>
              <button className={!estimate?'on':''} onClick={()=>setEstimate(false)}>Final Invoice</button>
            </div>
          </div>
          <div className="field">
            <label>Copies</label>
            <div style={{display:'grid', gap:4}}>
              <label className="row" style={{fontSize:12, gap:6}}><input type="checkbox" checked={copies.original} onChange={e=>setCopies(c=>({...c, original:e.target.checked}))}/> Original (for customer)</label>
              <label className="row" style={{fontSize:12, gap:6}}><input type="checkbox" checked={copies.duplicate} onChange={e=>setCopies(c=>({...c, duplicate:e.target.checked}))}/> Duplicate (for showroom)</label>
              <label className="row" style={{fontSize:12, gap:6}}><input type="checkbox" checked={copies.triplicate} onChange={e=>setCopies(c=>({...c, triplicate:e.target.checked}))}/> Triplicate (for transport)</label>
            </div>
          </div>
          <div className="field">
            <label>Printer</label>
            <select className="select" value={printer} onChange={e=>setPrinter(e.target.value)}>
              <option>Epson TM-U220 (Counter-01)</option>
              <option>HP LaserJet M1005 (Office)</option>
              <option>Microsoft Print to PDF</option>
            </select>
          </div>
          <div className="field">
            <label>Paper</label>
            <select className="select"><option>A5 · Portrait</option><option>A4 · Portrait</option><option>80mm thermal</option></select>
          </div>
          <div style={{marginTop:'auto', fontSize:11, color:'var(--ink-muted)', background:'var(--bg-sunken)', padding:8, borderRadius:3, border:'1px solid var(--border)'}}>
            {estimate
              ? 'Estimates are not tax invoices and do not post to Tally. Use Final Invoice after the customer confirms.'
              : 'Printing the final invoice will post this voucher to Tally (once queue is clear).'}
          </div>
        </div>
      </div>
    </Dialog>
  );
}

function PostSaveDialog({ onClose }) {
  return (
    <Dialog
      title={<><span>Bill Saved</span> <span className="chip ok" style={{marginLeft:6}}>● SR/25-26/0143</span></>}
      width={460}
      onClose={onClose}
      footer={<>
        <button className="btn" onClick={onClose}>Skip <span className="kbd kbd-inline">S</span></button>
        <div className="spacer" />
        <button className="btn" onClick={onClose}>Preview <span className="kbd kbd-inline">V</span></button>
        <button className="btn primary" onClick={onClose} autoFocus>Print Directly <span className="kbd kbd-inline">Enter</span></button>
      </>}
    >
      <div style={{display:'grid', gap:10, fontSize:12.5}}>
        <div>Bill <b className="mono">SR/25-26/0143</b> was saved and queued for Tally posting.</div>
        <div style={{background:'var(--bg-sunken)', padding:10, borderRadius:3, fontSize:11.5, color:'var(--ink-muted)', border:'1px solid var(--border)'}}>
          <div className="row"><span className="dot warn" /> Tally is unavailable — retry posting manually after recovery.</div>
          <div className="row" style={{marginTop:4}}><span className="dot ok" /> Customer copy will print immediately.</div>
        </div>
      </div>
    </Dialog>
  );
}

function ConfirmRepostDialog({ onClose, billNo = 'DDAJR/26-27/46' }) {
  React.useEffect(() => {
    const onKey = (e) => {
      if (e.key === 'Escape') onClose();
      if (e.key === 'Enter') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="scrim" onClick={(e)=>{ if(e.target.classList.contains('scrim')) onClose(); }}>
      <div className="winmsg" role="dialog" aria-label="Confirm Repost">
        <div className="winmsg-head">
          <div className="winmsg-title">Confirm Repost</div>
          <button className="winmsg-x" onClick={onClose} aria-label="Close">
            <svg width="10" height="10" viewBox="0 0 10 10"><path d="M1 1 L9 9 M9 1 L1 9" stroke="currentColor" strokeWidth="1.1" strokeLinecap="round" /></svg>
          </button>
        </div>
        <div className="winmsg-body">
          <div className="winmsg-icon" aria-hidden="true">
            {/* Classic Windows query icon — blue circle with white question mark */}
            <svg width="32" height="32" viewBox="0 0 32 32">
              <defs>
                <radialGradient id="winq" cx="0.35" cy="0.3" r="0.85">
                  <stop offset="0%" stopColor="#7ec1ff"/>
                  <stop offset="55%" stopColor="#2f7fd1"/>
                  <stop offset="100%" stopColor="#1c5ea0"/>
                </radialGradient>
              </defs>
              <circle cx="16" cy="16" r="14" fill="url(#winq)" stroke="#0f3a6b" strokeWidth="0.6"/>
              <text x="16" y="22" fontFamily="Segoe UI, Tahoma, sans-serif" fontWeight="700" fontSize="20" fill="#fff" textAnchor="middle">?</text>
            </svg>
          </div>
          <div className="winmsg-text">Repost <span className="mono">{billNo}</span>? This will push another voucher to Tally.</div>
        </div>
        <div className="winmsg-foot">
          <button className="winmsg-btn primary" onClick={onClose} autoFocus>OK</button>
          <button className="winmsg-btn" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

function AdminUnlockDialog({ onClose }) {
  return (
    <Dialog
      title={<><span>Administrator Unlock</span> <span className="chip err" style={{marginLeft:8}}>● LOCKED</span></>}
      width={520}
      onClose={onClose}
      footer={<>
        <div className="spacer" />
        <button className="btn" onClick={onClose}>Close</button>
      </>}
    >
      <div style={{display:'grid', gap:10, fontSize:12.5}}>
        <div style={{fontSize:10, fontWeight:700, letterSpacing:'0.08em', textTransform:'uppercase', color:'var(--ink-soft)', marginBottom:2}}>Unlock</div>
        <div className="field"><label>Passcode</label><input className="input" type="password" autoFocus /></div>
        <div className="row" style={{gap:10, marginTop:4}}>
          <button className="btn primary">Unlock</button>
          <span style={{fontSize:11.5, color:'var(--ink-muted)'}}>Unlock is valid for 30 minutes on this counter only.</span>
        </div>
      </div>
    </Dialog>
  );
}

function DangerConfirmDialog({ onClose }) {
  const [typed, setTyped] = React.useState('');
  return (
    <Dialog
      title={<><span style={{color:'var(--err)'}}>⚠ Delete Local Draft Bills — FY 2025-26</span></>}
      width={500}
      onClose={onClose}
      footer={<>
        <button className="btn" onClick={onClose} autoFocus>Cancel <span className="kbd kbd-inline">Esc</span></button>
        <div className="spacer" />
        <button className="btn danger filled" disabled={typed !== 'DELETE'}>I understand — Delete Drafts</button>
      </>}
    >
      <div style={{display:'grid', gap:10, fontSize:12.5}}>
        <div style={{background:'var(--err-soft)', border:'1px solid color-mix(in oklab, var(--err) 35%, transparent)', padding:10, borderRadius:3}}>
          <b>This cannot be undone.</b> You are about to delete <b className="mono">143 local draft bills</b> totalling <b className="mono">₹18,42,180</b>.
        </div>
        <ul style={{margin:0, paddingLeft:16, color:'var(--ink-muted)', fontSize:12}}>
          <li>Tally company data is <b>not</b> touched.</li>
          <li>Audit history for posted bills is preserved.</li>
          <li>Masters (parties, items, ledgers) are preserved.</li>
        </ul>
        <div className="field"><label>Type <span className="mono" style={{color:'var(--err)'}}>DELETE</span> to confirm</label><input className="input mono" value={typed} onChange={e=>setTyped(e.target.value)} placeholder="DELETE" /></div>
      </div>
    </Dialog>
  );
}

function ShortcutsDialog({ onClose }) {
  const groups = [
    { name: 'Navigation', items: [
      ['Invoice screen', 'Ctrl+1'],
      ['Bills screen', 'Ctrl+2'],
      ['Settings', 'Ctrl+3'],
      ['Focus search', '/'],
      ['This help', '?'],
    ]},
    { name: 'Invoice entry', items: [
      ['New line', 'Ctrl+N'],
      ['Remove line', 'Ctrl+Del'],
      ['Next field', 'Tab'],
      ['Prev field', 'Shift+Tab'],
      ['Next row', 'Enter'],
      ['Move row', '↑ / ↓'],
      ['Quick-add picker', 'F3'],
      ['Edit party', 'F4'],
      ['Edit 24kt rate', 'F2'],
    ]},
    { name: 'Actions', items: [
      ['Save & Post', 'Ctrl+S'],
      ['Print Estimate', 'F9'],
      ['Print Final', 'Ctrl+P'],
      ['Clear / Cancel', 'Esc Esc'],
    ]},
    { name: 'Bills screen', items: [
      ['Open details', 'Enter'],
      ['Retry sync', 'R'],
      ['Post', 'P'],
      ['Repost', '⇧P'],
      ['Edit', 'E'],
    ]},
  ];
  return (
    <Dialog
      title="Keyboard Shortcuts"
      width={620}
      onClose={onClose}
      footer={<><div className="spacer"/><button className="btn primary" onClick={onClose}>Close <span className="kbd kbd-inline">Esc</span></button></>}
    >
      <div className="kbd-help-grid">
        {groups.map(g => (
          <div key={g.name}>
            <div className="section-title" style={{paddingBottom:4, borderBottom:'1px solid var(--divider)', marginBottom:4}}>{g.name}</div>
            {g.items.map(([label, key]) => (
              <div className="kbd-help-row" key={label}>
                <span>{label}</span>
                <span>{key.split(/\s/).map((k,i)=><React.Fragment key={i}>{i>0 && ' '}<span className="kbd">{k}</span></React.Fragment>)}</span>
              </div>
            ))}
          </div>
        ))}
      </div>
    </Dialog>
  );
}

Object.assign(window, { Dialog, BillDetailsDialog, PrintPreviewDialog, PostSaveDialog, AdminUnlockDialog, DangerConfirmDialog, ShortcutsDialog, ConfirmRepostDialog });
