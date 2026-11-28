// Sample data for Tally Wrapper
window.SAMPLE = (() => {
  const items = [
    { sku: 'CHN-22-045', name: '22kt Gold Chain (Rope, 18")', karat: '22kt', unit: 'gm', wt: 12.450, wastage: 8, labour: 320, rate: 7125 },
    { sku: 'BNG-22-112', name: '22kt Bangle Set (pair)',     karat: '22kt', unit: 'gm', wt: 28.800, wastage: 10, labour: 450, rate: 7125 },
    { sku: 'ERG-18-034', name: '18kt Diamond Stud Earring',   karat: '18kt', unit: 'pc', wt: 3.260,  wastage: 0,  labour: 1800, rate: 5830 },
    { sku: 'PND-22-021', name: '22kt Lakshmi Pendant',        karat: '22kt', unit: 'gm', wt: 4.120,  wastage: 9,  labour: 260, rate: 7125 },
    { sku: 'RNG-22-077', name: '22kt Ladies Ring',            karat: '22kt', unit: 'gm', wt: 5.680,  wastage: 12, labour: 280, rate: 7125 },
    { sku: 'CHN-18-052', name: '18kt Rose-Gold Chain',        karat: '18kt', unit: 'gm', wt: 8.240,  wastage: 7,  labour: 220, rate: 5830 },
    { sku: 'BGL-22-118', name: '22kt Kada (Mens)',            karat: '22kt', unit: 'gm', wt: 42.650, wastage: 6,  labour: 550, rate: 7125 },
    { sku: 'NCK-22-003', name: '22kt Mangalsutra',            karat: '22kt', unit: 'gm', wt: 16.240, wastage: 10, labour: 380, rate: 7125 },
    { sku: 'CHN-24-009', name: '24kt Gold Coin 8g',           karat: '24kt', unit: 'pc', wt: 8.000,  wastage: 0,  labour: 120, rate: 7780 },
    { sku: 'SLV-92-201', name: '92.5 Silver Anklet',          karat: 'Silver', unit: 'gm', wt: 74.200, wastage: 4, labour: 80, rate: 95 },
  ];

  // Fictional invoices for the grouped history and bill-detail examples.
  const bills = [
    // Today - Sat, 25 Apr 2026 (1)
    { no: 'DEMO/26-27/50', status: 'posted',  edited: true,  party: 'Walk-in Customer',     amount: 103000.00, date: '2026-04-25', updated: '2026-04-25 12:15', err: 'Edited after push' },
    // Wed, 22 Apr 2026 (1)
    { no: 'DEMO/26-27/49', status: 'posted',  party: 'Cash',                                amount: 82400.00, date: '2026-04-22', updated: '2026-04-24 11:30', err: null },
    // Tue, 21 Apr 2026 (2)
    { no: 'DEMO/26-27/47', status: 'posted',  party: 'Cash',                                amount: 51500.00,   date: '2026-04-21', updated: '2026-04-21 13:18', err: null },
    { no: 'DEMO/26-27/46', status: 'posted',  party: 'Cash',                                amount: 30900.00,  date: '2026-04-21', updated: '2026-04-21 13:18', err: null },
    // Mon, 20 Apr 2026 (3)
    { no: 'DEMO/26-27/45', status: 'posted',  party: 'Cash',                                amount: 61800.00,  date: '2026-04-20', updated: '2026-04-20 13:55', err: null },
    { no: 'DEMO/26-27/44', status: 'posted',  party: 'Cash',                                amount: 25750.00,  date: '2026-04-20', updated: '2026-04-20 13:55', err: null },
    { no: 'DEMO/26-27/43', status: 'posted',  party: 'Credit and Debit',                    amount: 20600.00,  date: '2026-04-20', updated: '2026-04-20 13:55', err: null },
    // Sun, 19 Apr 2026 (9)
    { no: 'DEMO/26-27/42', status: 'posted',  party: 'Cash',                                amount: 41200.00,  date: '2026-04-19', updated: '2026-04-19 15:34', err: null },
    { no: 'DEMO/26-27/41', status: 'posted',  party: 'Cash',                                amount: 72100.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/40', status: 'posted',  party: 'Cash',                                amount: 56650.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/39', status: 'posted',  party: 'Cash',                                amount: 66950.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/38', status: 'posted',  party: 'Cash',                                amount: 15450.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/37', status: 'posted',  party: 'Cash',                                amount: 36050.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/36', status: 'posted',  party: 'Sample Customer A',                   amount: 87550.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/35', status: 'posted',  party: 'Sample Customer B',                   amount: 46350.00, date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    { no: 'DEMO/26-27/34', status: 'posted',  party: 'Credit and Debit',                    amount: 77250.00,  date: '2026-04-19', updated: '2026-04-19 15:23', err: null },
    // Sat, 18 Apr 2026 (2)
    { no: 'DEMO/26-27/33', status: 'posted',  party: 'Credit and Debit',                    amount: 92700.00, date: '2026-04-18', updated: '2026-04-19 08:41', err: null },
    { no: 'DEMO/26-27/32', status: 'posted',  party: 'Credit and Debit',                    amount: 97850.00, date: '2026-04-18', updated: '2026-04-19 08:41', err: null },
  ];

  // Parties
  const parties = [
    'Meera Subramaniam', 'Ramesh & Co (Wholesale)', 'Kavita Iyer', 'Anand Pillai',
    'Walk-in Customer', 'Lakshmi Narayanan', 'Priya Venkatesh', 'R. Subramaniam HUF',
    'Deepa Raghavan', 'Arjun Srinivas', 'Vijaya Stores', 'Karthik Ramanathan'
  ];

  return { items, bills, parties };
})();

window.fmtINR = function(n, decimals = 2) {
  if (n === null || n === undefined || isNaN(n)) return '';
  const x = Math.abs(n);
  const neg = n < 0 ? '-' : '';
  const s = x.toFixed(decimals);
  const [int, frac] = s.split('.');
  // Indian grouping: last 3 then 2s
  const last3 = int.slice(-3);
  const rest = int.slice(0, -3);
  const grouped = rest ? rest.replace(/\B(?=(\d{2})+(?!\d))/g, ',') + ',' + last3 : last3;
  return neg + grouped + (decimals ? '.' + frac : '');
};
