// Invoice screen — Gross WT / Less WT / Diamond R / keyboard-first
function InvoiceScreen({ systemState, editingPosted, onOpenDialog, setLineCount, setLastSaved }) {
  const [lines, setLines] = React.useState(() => [
    { id: 1, name: '22kt Gold Chain (Rope, 18")', qty: 1, grossWt: 12.450, lessWt: 0,     unit: 'gm', karat: '22kt',   wastage: 8,  labour: 320, diamondR: 0, extra: 0 },
    { id: 2, name: '22kt Lakshmi Pendant',        qty: 1, grossWt: 4.120,  lessWt: 0,     unit: 'gm', karat: '22kt',   wastage: 9,  labour: 260, diamondR: 0, extra: 0 },
    { id: 3, name: 'Diamond Ring',                qty: 1, grossWt: 5.200,  lessWt: 1.400, unit: 'gm', karat: '18kt',   wastage: 0,  labour: 1800, diamondR: 42000, extra: 0 },
    { id: 4, name: '', qty: '', grossWt: '', lessWt: '', unit: 'gm', karat: '22kt', wastage: '', labour: '', diamondR: '', extra: '' },
  ]);
  const [header, setHeader] = React.useState({
    no: 'SR/25-26/0143', date: '23-Apr-2026', party: 'Meera Subramaniam',
    payment: 'Cash', rate24: 7780, narration: '', discount: 500,
  });
  const [focusRow, setFocusRow]   = React.useState(0);
  const [pickerQuery, setPickerQuery] = React.useState('');
  const [showSavedToast, setShowSavedToast] = React.useState(false);
  const [ac, setAc] = React.useState({ show: false, items: [], rowIdx: -1, x: 0, y: 0, w: 0, selIdx: 0 });

  React.useEffect(() => { setLineCount(lines.filter(l => l.name).length); }, [lines]);

  const rateForKarat = k => ({ '24kt': 7780, '22kt': 7125, '18kt': 5830, Silver: 95 }[k] || 7125);

  const computeLine = (l) => {
    if (!l.name) return { effRate: 0, netWt: 0, total: 0 };
    const base      = rateForKarat(l.karat);
    const wastage   = (Number(l.wastage) || 0) / 100;
    const effRate   = base * (1 + wastage);
    const grossWt   = Number(l.grossWt) || 0;
    const lessWt    = Number(l.lessWt)  || 0;
    const netWt     = Math.max(0, grossWt - lessWt);
    const qty       = Number(l.qty) || 0;
    const labour    = Number(l.labour)  || 0;
    const diamondR  = Number(l.diamondR) || 0;
    const extra     = Number(l.extra)   || 0;
    const mat       = l.unit === 'pc' ? qty * effRate : netWt * effRate;
    const making    = (l.unit === 'pc' ? qty : netWt) * labour;
    return { effRate, netWt, total: mat + making + diamondR + extra };
  };

  const subtotal   = lines.reduce((a, l) => a + computeLine(l).total, 0);
  const cgst       = subtotal * 0.015;
  const sgst       = subtotal * 0.015;
  const discount   = Number(header.discount) || 0;
  const preRound   = subtotal + cgst + sgst - discount;
  const grand      = Math.round(preRound);
  const roundOff   = grand - preRound;

  // ── Tab order: 0=name 1=grossWt 2=lessWt 3=unit 4=karat 5=wastage 6=labour 7=diamondR 8=extra
  const LAST_COL = 8;
  const focusCell = (row, col) => {
    setTimeout(() => {
      const el = document.querySelector(`[data-inv-row="${row}"][data-inv-col="${col}"]`);
      if (el) { el.focus(); if (el.select) el.select(); }
    }, 20);
  };

  const handleCellKey = (e, rowIdx, colIdx) => {
    if (e.key === 'Tab') {
      e.preventDefault(); hideAc();
      if (!e.shiftKey) {
        if (colIdx < LAST_COL) focusCell(rowIdx, colIdx + 1);
        else { const nr = rowIdx + 1; if (nr >= lines.length) addRow(); focusCell(nr, 0); }
      } else {
        if (colIdx > 0) focusCell(rowIdx, colIdx - 1);
        else if (rowIdx > 0) focusCell(rowIdx - 1, LAST_COL);
      }
    }
    if (e.key === 'Enter' && !ac.show) {
      e.preventDefault();
      const nr = rowIdx + 1;
      if (nr >= lines.length) addRow();
      focusCell(nr, colIdx);
    }
    if (ac.show) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setAc(a => ({...a, selIdx: Math.min(a.selIdx+1, a.items.length-1)})); }
      if (e.key === 'ArrowUp')   { e.preventDefault(); setAc(a => ({...a, selIdx: Math.max(a.selIdx-1, 0)})); }
      if (e.key === 'Enter') { e.preventDefault(); if (ac.items[ac.selIdx]) { applyAcItem(ac.items[ac.selIdx], ac.rowIdx); focusCell(ac.rowIdx, 1); } }
      if (e.key === 'Escape') { e.preventDefault(); hideAc(); }
    }
    if (!ac.show) {
      if (e.key === 'ArrowUp'   && rowIdx > 0)                  { e.preventDefault(); focusCell(rowIdx - 1, colIdx); }
      if (e.key === 'ArrowDown' && rowIdx < lines.length - 1)   { e.preventDefault(); focusCell(rowIdx + 1, colIdx); }
    }
  };

  const showAc = (e, rowIdx) => {
    const q = e.target.value.toLowerCase().trim();
    if (!q) { hideAc(); return; }
    const items = SAMPLE.items.filter(i => i.name.toLowerCase().includes(q)).slice(0, 8);
    if (!items.length) { hideAc(); return; }
    const r = e.target.getBoundingClientRect();
    setAc({ show: true, items, rowIdx, x: r.left, y: r.bottom + 2, w: Math.max(r.width, 360), selIdx: 0 });
  };
  const hideAc = () => setAc(a => ({...a, show: false}));
  const applyAcItem = (item, rowIdx) => {
    setLines(ls => ls.map((l, i) => i !== rowIdx ? l : {
      ...l, name: item.name,
      qty: l.qty || 1, grossWt: item.wt, lessWt: 0,
      unit: item.unit, karat: item.karat, wastage: item.wastage, labour: item.labour,
    }));
    hideAc();
  };

  const addRow = () => setLines(ls => [...ls, { id: Date.now(), name: '', qty: '', grossWt: '', lessWt: '', unit: 'gm', karat: '22kt', wastage: '', labour: '', diamondR: '', extra: '' }]);
  const removeRow = (idx) => {
    setLines(ls => ls.length === 1 ? ls : ls.filter((_, i) => i !== idx));
    setFocusRow(r => Math.max(0, r - (idx <= r ? 1 : 0)));
  };
  const updateLine = (idx, key, val) => setLines(ls => ls.map((l, i) => i === idx ? { ...l, [key]: val } : l));

  const addItemFromPicker = (item) => {
    setLines(ls => {
      const idx = ls.findIndex(l => !l.name);
      const nl = { id: Date.now(), name: item.name, qty: 1, grossWt: item.wt, lessWt: 0, unit: item.unit, karat: item.karat, wastage: item.wastage, labour: item.labour, diamondR: 0, extra: 0 };
      if (idx >= 0) {
        const next = [...ls]; next[idx] = nl;
        if (!next.some(l => !l.name)) next.push({ id: Date.now()+1, name: '', qty: '', grossWt: '', lessWt: '', unit: 'gm', karat: '22kt', wastage: '', labour: '', diamondR: '', extra: '' });
        return next;
      }
      return [...ls, nl];
    });
  };

  const save = () => {
    setShowSavedToast(true);
    setLastSaved(new Date().toLocaleTimeString('en-IN', { hour12: false }));
    setTimeout(() => setShowSavedToast(false), 1800);
    setTimeout(() => onOpenDialog('postSave'), 200);
  };

  React.useEffect(() => {
    const h = (e) => {
      if ((e.ctrlKey||e.metaKey) && e.key === 's') { e.preventDefault(); save(); }
      if ((e.ctrlKey||e.metaKey) && e.key === 'n') { e.preventDefault(); addRow(); }
      if (e.key === 'F9') { e.preventDefault(); onOpenDialog('print'); }
    };
    window.addEventListener('keydown', h); return () => window.removeEventListener('keydown', h);
  }, [lines]);

  React.useEffect(() => {
    const h = (e) => { if (!e.target.closest('[data-inv-ac]')) hideAc(); };
    window.addEventListener('mousedown', h); return () => window.removeEventListener('mousedown', h);
  }, []);

  const filteredPicker = React.useMemo(() => {
    const q = pickerQuery.trim().toLowerCase();
    return q ? SAMPLE.items.filter(i => i.name.toLowerCase().includes(q)) : SAMPLE.items;
  }, [pickerQuery]);

  const cellProps = (rowIdx, colIdx) => ({
    'data-inv-row': rowIdx, 'data-inv-col': colIdx,
    onFocus: () => setFocusRow(rowIdx),
    onKeyDown: (e) => handleCellKey(e, rowIdx, colIdx),
  });

  return (
    <div style={{ display:'grid', gridTemplateColumns:'1fr 220px', height:'100%', minHeight:0, overflow:'hidden' }}>
      <div style={{display:'flex', flexDirection:'column', minHeight:0, overflow:'hidden'}}>

        {editingPosted && (
          <div style={{
            padding:'5px 14px',
            background:'#fef3c7',
            borderBottom:'1px solid #f0c674',
            color:'#7a4f01',
            fontSize:11.5,
            display:'flex',
            alignItems:'center',
            gap:8,
          }}>
            <span style={{fontStyle:'italic'}}>Editing a previously-posted bill. Saving moves it back to pending and re-queues the new revision to Tally.</span>
          </div>
        )}

        {/* Header */}
        <div style={{display:'grid', gridTemplateColumns:'1.2fr 0.9fr 0.85fr 0.95fr 1.5fr', gap:'8px 14px', padding:'10px 14px', background:'var(--bg-panel)', borderBottom:'1px solid var(--border)'}}>
          <div className="field"><label>Invoice # <span className="kbd kbd-inline">auto</span></label><input className="input mono readonly" value={header.no} readOnly /></div>
          <div className="field"><label>Date</label><input className="input mono" value={header.date} onChange={e=>setHeader(h=>({...h,date:e.target.value}))} /></div>
          <div className="field"><label>Payment</label>
            <select className="select" value={header.payment} onChange={e=>setHeader(h=>({...h,payment:e.target.value}))}>
              <option>Cash</option><option>UPI</option><option>Card</option><option>Bank Transfer</option><option>Credit</option>
            </select>
          </div>
          <div className="field"><label>24kt Rate (₹/g) <span className="kbd kbd-inline">F2</span></label><input className="input mono tnum" value={header.rate24} onChange={e=>setHeader(h=>({...h,rate24:e.target.value}))} /></div>
          <div className="field">
            <label>Party <span style={{color:'var(--err)'}}>*</span> <span className="kbd kbd-inline">F4</span></label>
            <input className="input" value={header.party} onChange={e=>setHeader(h=>({...h,party:e.target.value}))} list="party-list" placeholder="Party name…" />
            <datalist id="party-list">{SAMPLE.parties.map(p=><option key={p} value={p}/>)}</datalist>
          </div>
        </div>

        {/* Line item toolbar */}
        <div style={{display:'flex', alignItems:'center', gap:8, padding:'5px 14px', background:'var(--bg-sunken)', borderBottom:'1px solid var(--border)'}}>
          <span style={{fontSize:10, fontWeight:700, textTransform:'uppercase', letterSpacing:'0.06em', color:'var(--ink-soft)', marginRight:'auto'}}>Line Items</span>
          <button className="btn sm" onClick={()=>{addRow(); setTimeout(()=>focusCell(lines.length,0),30);}}>+ Add Row <span className="kbd kbd-inline">Ctrl+N</span></button>
          <button className="btn sm" onClick={()=>removeRow(focusRow)}>− Remove <span className="kbd kbd-inline">Ctrl+Del</span></button>
          <span className="divider-v" style={{height:16}}/>
          <span className="hint"><span className="kbd">Tab</span> next field · <span className="kbd">Enter</span> next row · <span className="kbd">↑↓</span> move</span>
        </div>

        {/* Table */}
        <div style={{flex:1, overflowY:'auto', overflowX:'auto', background:'var(--bg-panel)'}} data-inv-ac>
          <table className="dt" style={{minWidth:900}}>
            <thead>
              <tr>
                <th style={{width:28,paddingLeft:10}}>#</th>
                <th style={{minWidth:180}}>Item / Stock Name</th>
                <th style={{width:52}}>Qty</th>
                <th style={{width:78}}>Gross Wt</th>
                <th style={{width:72}}>Less Wt</th>
                <th style={{width:50}}>Unit</th>
                <th style={{width:62}}>Karat</th>
                <th style={{width:68}}>Wastage%</th>
                <th style={{width:76}}>Labour</th>
                <th style={{width:86}}>Diamond R</th>
                <th style={{width:68}}>Extra</th>
                <th style={{width:90}}>Eff. Rate</th>
                <th style={{width:108}} className="num">Line Total</th>
                <th style={{width:26}}></th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, idx) => {
                const { effRate, netWt, total } = computeLine(l);
                const isFocus = idx === focusRow;
                return (
                  <tr key={l.id} className={`entry-row ${isFocus?'focus':''}`} onClick={()=>setFocusRow(idx)}>
                    <td style={{paddingLeft:10, color:'var(--ink-muted)', fontFamily:'JetBrains Mono,monospace', fontSize:11}}>{String(idx+1).padStart(2,'0')}</td>
                    <td>
                      <input className="rcell" value={l.name} placeholder="Type to search item…"
                        onChange={e=>{updateLine(idx,'name',e.target.value); showAc(e,idx);}}
                        {...cellProps(idx,0)} />
                    </td>
                    <td><input className="rcell mono" style={{textAlign:'right'}} value={l.qty}     onChange={e=>updateLine(idx,'qty',e.target.value)}     {...cellProps(idx,1)} /></td>
                    <td><input className="rcell mono" style={{textAlign:'right'}} value={l.grossWt} onChange={e=>updateLine(idx,'grossWt',e.target.value)} {...cellProps(idx,1)} /></td>
                    <td><input className="rcell mono" style={{textAlign:'right', color: Number(l.lessWt)>0 ? 'var(--warn)' : ''}} value={l.lessWt} onChange={e=>updateLine(idx,'lessWt',e.target.value)} {...cellProps(idx,2)} /></td>
                    <td>
                      <select className="rcell" style={{border:'none',background:'transparent'}} value={l.unit} onChange={e=>updateLine(idx,'unit',e.target.value)} {...cellProps(idx,3)}>
                        <option value="gm">gm</option><option value="pc">pc</option><option value="ct">ct</option>
                      </select>
                    </td>
                    <td>
                      <select className="rcell" style={{border:'none',background:'transparent'}} value={l.karat} onChange={e=>updateLine(idx,'karat',e.target.value)} {...cellProps(idx,4)}>
                        <option>24kt</option><option>22kt</option><option>20kt</option><option>18kt</option><option>Silver</option>
                      </select>
                    </td>
                    <td><input className="rcell mono" style={{textAlign:'right'}} value={l.wastage}  onChange={e=>updateLine(idx,'wastage',e.target.value)}  {...cellProps(idx,5)} /></td>
                    <td><input className="rcell mono" style={{textAlign:'right'}} value={l.labour}   onChange={e=>updateLine(idx,'labour',e.target.value)}   {...cellProps(idx,6)} /></td>
                    <td><input className="rcell mono" style={{textAlign:'right', color: Number(l.diamondR)>0?'var(--accent)':''}} value={l.diamondR} onChange={e=>updateLine(idx,'diamondR',e.target.value)} {...cellProps(idx,7)} /></td>
                    <td><input className="rcell mono" style={{textAlign:'right'}} value={l.extra}    onChange={e=>updateLine(idx,'extra',e.target.value)}    {...cellProps(idx,8)} /></td>
                    <td style={{textAlign:'right', color:'var(--ink-muted)', fontFamily:'JetBrains Mono,monospace', fontSize:11}}>
                      {l.name ? <>₹{fmtINR(effRate,0)}{netWt>0&&<div style={{fontSize:9,color:'var(--ink-soft)'}}>{fmtINR(netWt,3)}g</div>}</> : ''}
                    </td>
                    <td className="num mono" style={{fontWeight:600}}>{l.name ? '₹'+fmtINR(total) : ''}</td>
                    <td>{l.name && <button className="btn ghost sm" onClick={e=>{e.stopPropagation();removeRow(idx);}} style={{width:22,padding:0,height:22,fontSize:14}}>×</button>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>

          {/* Autocomplete */}
          {ac.show && (
            <div data-inv-ac style={{position:'fixed', top:ac.y, left:ac.x, width:ac.w, background:'var(--bg-panel)', border:'1px solid var(--border-strong)', borderRadius:4, boxShadow:'var(--shadow-dialog)', zIndex:150, overflow:'hidden'}}>
              {ac.items.map((item, i) => (
                <div key={item.name}
                  onMouseDown={e=>{e.preventDefault(); applyAcItem(item, ac.rowIdx); focusCell(ac.rowIdx,1);}}
                  style={{padding:'6px 10px', cursor:'pointer', background: i===ac.selIdx?'var(--accent-soft)':'var(--bg-panel)', boxShadow: i===ac.selIdx?'inset 3px 0 0 var(--accent)':'none', borderBottom:'1px solid var(--divider)', fontSize:'var(--font-ui)', fontWeight: i===ac.selIdx?500:400}}>
                  {item.name}
                </div>
              ))}
              <div style={{padding:'3px 10px', background:'var(--bg-sunken)', fontSize:10, color:'var(--ink-muted)', display:'flex', gap:5}}>
                <span className="kbd">↑↓</span> select · <span className="kbd">Enter</span> pick · <span className="kbd">Esc</span> close
              </div>
            </div>
          )}
        </div>

        {/* Footer */}
        <div style={{display:'grid', gridTemplateColumns:'1fr 300px', borderTop:'1px solid var(--border)', background:'var(--bg-panel)'}}>
          <div style={{padding:'10px 14px', borderRight:'1px solid var(--border)'}}>
            <div className="field">
              <label>Narration / Notes</label>
              <textarea className="textarea" rows="3" placeholder="Hallmark BIS 916. Exchange of old jewellery noted separately." value={header.narration} onChange={e=>setHeader(h=>({...h,narration:e.target.value}))} />
            </div>
            <div style={{marginTop:8, display:'flex', gap:16, color:'var(--ink-muted)', fontSize:11}}>
              <span>Items: <b className="mono" style={{color:'var(--ink)'}}>{lines.filter(l=>l.name).length}</b></span>
              <span>Gross: <b className="mono" style={{color:'var(--ink)'}}>{fmtINR(lines.reduce((a,l)=>a+(Number(l.grossWt)||0),0),3)}g</b></span>
              <span>Net: <b className="mono" style={{color:'var(--ink)'}}>{fmtINR(lines.reduce((a,l)=>a+computeLine(l).netWt,0),3)}g</b></span>
              <span>HSN: <b className="mono" style={{color:'var(--ink)'}}>7113</b></span>
              <span>Supply: <b style={{color:'var(--ink)'}}>Tamil Nadu (33)</b></span>
            </div>
          </div>
          <div style={{padding:'10px 14px'}}>
            <div className="invoice-totals">
              <div className="lbl">Subtotal</div><div className="val">₹{fmtINR(subtotal)}</div>
              <div className="lbl">CGST @ 1.5%</div><div className="val">₹{fmtINR(cgst)}</div>
              <div className="lbl">SGST @ 1.5%</div><div className="val">₹{fmtINR(sgst)}</div>
              <div className="lbl">Discount</div>
              <div className="val" style={{color:'var(--err)'}}>
                − <input className="mono" style={{width:68,textAlign:'right',border:'none',background:'transparent',color:'var(--err)',fontFamily:'JetBrains Mono,monospace'}} value={header.discount} onChange={e=>setHeader(h=>({...h,discount:e.target.value}))}/>
              </div>
              <div className="lbl">Round Off</div><div className="val" style={{color:'var(--ink-muted)'}}>{roundOff>=0?'+':''}{fmtINR(roundOff)}</div>
              <div className="lbl grand">Grand Total</div><div className="val grand">₹{fmtINR(grand,0)}</div>
            </div>
          </div>
        </div>

        {/* Actions */}
        <div style={{display:'flex',alignItems:'center',gap:8,padding:'8px 14px',borderTop:'1px solid var(--border)',background:'var(--bg-sunken)'}}>
          <button className="btn" onClick={()=>onOpenDialog('print')}>Print Estimate <span className="kbd kbd-inline">F9</span></button>
          <button className="btn">Clear / Cancel <span className="kbd kbd-inline">Esc Esc</span></button>
          {editingPosted && (
            <span style={{fontSize:11.5, color:'#7a4f01', fontStyle:'italic'}}>
              Edit'ing <span className="mono" style={{fontStyle:'normal'}}>{header.no}</span> (was posted). Save re-queues to Tally.
            </span>
          )}
          <div className="spacer"/>
          <span style={{fontSize:11.5,color:'var(--ink-muted)',marginRight:4}}>Grand <b className="mono" style={{color:'var(--ink)',fontSize:14}}>₹{fmtINR(grand,0)}</b></span>
          <button className="btn primary" onClick={save} style={{minWidth:148}}>Save{editingPosted ? '' : ' & Post to Tally'} <span className="kbd kbd-inline">Ctrl+S</span></button>
        </div>
      </div>

      {/* Quick Add sidebar */}
      <aside style={{borderLeft:'1px solid var(--border)', background:'var(--bg-sunken)', display:'flex', flexDirection:'column', minHeight:0, overflow:'hidden'}}>
        <div style={{padding:'8px 10px', borderBottom:'1px solid var(--border)', background:'var(--bg-panel)'}}>
          <div style={{display:'flex',alignItems:'center',marginBottom:5}}>
            <span style={{fontSize:10,fontWeight:700,textTransform:'uppercase',letterSpacing:'0.06em',color:'var(--ink-soft)'}}>Quick Add</span>
            <span className="hint" style={{marginLeft:'auto'}}><span className="kbd">F3</span></span>
          </div>
          <input className="input" style={{fontSize:11.5}} placeholder="Search…" value={pickerQuery} onChange={e=>setPickerQuery(e.target.value)} />
        </div>
        <div style={{flex:1, overflowY:'auto'}}>
          {filteredPicker.map(it => (
            <div key={it.name}
              onDoubleClick={()=>addItemFromPicker(it)}
              style={{
                padding:'7px 10px',
                borderBottom:'1px solid var(--divider)',
                cursor:'pointer',
                fontSize:'var(--font-ui)',
              }}
              onMouseEnter={e=>e.currentTarget.style.background='var(--bg-hover)'}
              onMouseLeave={e=>e.currentTarget.style.background=''}
            >
              {it.name}
            </div>
          ))}
        </div>
        <div style={{padding:'6px 10px', borderTop:'1px solid var(--border)', background:'var(--bg-panel)', fontSize:10, color:'var(--ink-muted)'}}>
          Double-click to add · <span className="kbd">F3</span> focus
        </div>
      </aside>

      {showSavedToast && (
        <div className="toast">
          <span className="dot ok"/>
          <span>Bill <b className="mono">{header.no}</b> saved · queued for Tally</span>
        </div>
      )}
    </div>
  );
}

window.InvoiceScreen = InvoiceScreen;
