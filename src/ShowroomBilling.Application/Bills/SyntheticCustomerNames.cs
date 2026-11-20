namespace ShowroomBilling.Application.Bills;

/// <summary>
/// The customer names a synthetic bill is made out to (owner, 20 November 2026): a first name and a
/// surname from across India. A name is drawn from one region's names at a time, so a first name
/// and a surname that would not go together are never paired; men and women are drawn equally.
/// The name reaches Tally only as free text (the narration and the buyer's mailing name); the
/// voucher's party ledger comes from the payment mode.
/// </summary>
public static class SyntheticCustomerNames
{
    private sealed record Region(string[] Men, string[] Women, string[] Surnames);

    private static readonly Region[] Regions =
    [
        new( // Hindi-speaking north
            Men: ["Rahul", "Amit", "Rohit", "Vikas", "Sanjay", "Manoj", "Deepak", "Ankit", "Saurabh", "Pankaj", "Arun", "Rajesh"],
            Women: ["Priya", "Neha", "Pooja", "Anjali", "Sunita", "Kavita", "Ritu", "Shalini", "Swati", "Rekha", "Nisha", "Preeti"],
            Surnames: ["Sharma", "Verma", "Gupta", "Agarwal", "Yadav", "Chauhan", "Pandey", "Tiwari", "Mishra", "Srivastava", "Saxena", "Jain"]),
        new( // Punjab
            Men: ["Gurpreet", "Harpreet", "Manpreet", "Amandeep", "Hardeep", "Rajinder", "Sukhwinder", "Karan", "Arjun", "Jaspal"],
            Women: ["Simran", "Harleen", "Navneet", "Gurleen", "Jasleen", "Manjeet", "Kiran", "Rupinder", "Amrita", "Baljeet"],
            Surnames: ["Gill", "Sandhu", "Grewal", "Dhillon", "Bajwa", "Sidhu", "Arora", "Kapoor", "Khanna", "Malhotra"]),
        new( // Gujarat
            Men: ["Hitesh", "Jignesh", "Mehul", "Nilesh", "Bhavesh", "Ketan", "Chirag", "Paresh", "Dhaval", "Kalpesh"],
            Women: ["Hetal", "Komal", "Falguni", "Bhavna", "Dipti", "Jalpa", "Krupa", "Mansi", "Rupal", "Nayana"],
            Surnames: ["Patel", "Shah", "Mehta", "Desai", "Joshi", "Parikh", "Trivedi", "Modi", "Thakkar", "Panchal"]),
        new( // Maharashtra
            Men: ["Sachin", "Prasad", "Mahesh", "Sandeep", "Amol", "Tushar", "Nitin", "Ganesh", "Shrikant", "Vaibhav"],
            Women: ["Snehal", "Aarti", "Madhuri", "Ashwini", "Pallavi", "Sayali", "Supriya", "Manasi", "Vaishali", "Rupali"],
            Surnames: ["Patil", "Kulkarni", "Deshmukh", "Jadhav", "Pawar", "Shinde", "Gaikwad", "Deshpande", "More", "Chavan"]),
        new( // Bengal
            Men: ["Arnab", "Sourav", "Debashis", "Subhajit", "Abhijit", "Partha", "Anirban", "Tapas", "Sandip", "Indranil"],
            Women: ["Moumita", "Sreya", "Riya", "Paromita", "Sutapa", "Ananya", "Tanushree", "Debjani", "Rupa", "Sharmistha"],
            Surnames: ["Banerjee", "Chatterjee", "Mukherjee", "Das", "Ghosh", "Sen", "Bose", "Dutta", "Roy", "Chakraborty"]),
        new( // Odisha
            Men: ["Subrat", "Debasis", "Prasanta", "Bibhuti", "Sasmit", "Manas", "Ashutosh", "Biswajit", "Pradeep", "Sitansu"],
            Women: ["Sasmita", "Lipika", "Mamata", "Sangita", "Priyanka", "Itishree", "Jyotirmayee", "Smruti", "Banita", "Rasmita"],
            Surnames: ["Mohanty", "Panda", "Behera", "Patnaik", "Nayak", "Sahoo", "Mishra", "Pradhan", "Swain", "Jena"]),
        new( // Tamil Nadu
            Men: ["Karthik", "Senthil", "Murugan", "Saravanan", "Prakash", "Vignesh", "Balaji", "Ramesh", "Ganesh", "Sivakumar"],
            Women: ["Lakshmi", "Divya", "Meena", "Kavitha", "Revathi", "Sangeetha", "Deepa", "Anitha", "Gayathri", "Vasanthi"],
            Surnames: ["Iyer", "Pillai", "Subramanian", "Krishnan", "Raman", "Natarajan", "Sundaram", "Venkatesan", "Iyengar", "Rajan"]),
        new( // Andhra Pradesh and Telangana
            Men: ["Srinivas", "Venkatesh", "Ravi", "Suresh", "Naresh", "Kiran", "Praveen", "Chandra", "Satish", "Ramana"],
            Women: ["Sravani", "Lavanya", "Swathi", "Padma", "Sirisha", "Haritha", "Madhavi", "Sujatha", "Keerthi", "Bhavani"],
            Surnames: ["Reddy", "Naidu", "Rao", "Chowdary", "Varma", "Raju", "Goud", "Sastry", "Murthy", "Prasad"]),
        new( // Karnataka
            Men: ["Manjunath", "Raghavendra", "Prashanth", "Harish", "Naveen", "Shivakumar", "Girish", "Mohan", "Vinay", "Sunil"],
            Women: ["Shilpa", "Pavithra", "Rashmi", "Vidya", "Sowmya", "Kavya", "Asha", "Deepika", "Chaitra", "Nandini"],
            Surnames: ["Gowda", "Shetty", "Hegde", "Bhat", "Kamath", "Shenoy", "Pai", "Naik", "Acharya", "Rao"]),
        new( // Kerala
            Men: ["Anoop", "Sreejith", "Rajeev", "Vineeth", "Jithin", "Biju", "Sajeev", "Arun", "Manoj", "Shibu"],
            Women: ["Anju", "Lekha", "Remya", "Sreeja", "Aswathy", "Reshma", "Neethu", "Athira", "Sindhu", "Lakshmi"],
            Surnames: ["Nair", "Menon", "Pillai", "Kurup", "Varghese", "Thomas", "Kurian", "Mathew", "Panicker", "Warrier"]),
        new( // Muslim names, across India
            Men: ["Imran", "Salman", "Arif", "Faisal", "Irfan", "Javed", "Sameer", "Asif", "Rizwan", "Shahid"],
            Women: ["Ayesha", "Fatima", "Zoya", "Sana", "Nazia", "Rukhsar", "Shabana", "Farah", "Heena", "Nasreen"],
            Surnames: ["Khan", "Ahmed", "Qureshi", "Shaikh", "Ansari", "Siddiqui", "Syed", "Pathan", "Mirza", "Hussain"]),
    ];

    /// <summary>A first name and a surname, such as "Rahul Sharma" or "Lakshmi Iyer".</summary>
    public static string Pick(Random rng)
    {
        var region = Regions[rng.Next(Regions.Length)];
        var firstNames = rng.Next(2) == 0 ? region.Men : region.Women;
        return $"{firstNames[rng.Next(firstNames.Length)]} {region.Surnames[rng.Next(region.Surnames.Length)]}";
    }
}
